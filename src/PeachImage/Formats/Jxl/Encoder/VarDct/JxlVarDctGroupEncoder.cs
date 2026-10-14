using PeachImage.Formats.Jxl.Encoder.Entropy;
using PeachImage.Formats.Jxl.Encoder.Modular;
using PeachImage.Formats.Jxl.VarDct;

namespace PeachImage.Formats.Jxl.Encoder.VarDct;

/// <summary>
/// Encodes one 32x32-block AC group of a VarDCT frame: chooses the block transforms, transforms and quantizes every block,
/// fits the chroma-from-luma maps of the group's tiles, and emits the AC tokens in the order the decoder reads them.
/// Records the group's block types, quantization values, correlation factors and quantized DC in the shared frame state.
/// </summary>
internal static class JxlVarDctGroupEncoder
{
    private const int GroupBlocks = 32;
    private const int BlockSize = 64;
    private const byte Unassigned = 0xFE;

    // Strategy-search constants: bits per coded coefficient and the price of distortion in bits per squared step.
    private const double ZeroBits = 0.12;
    private const double CodedBits = 2.2;
    private const double LogBits = 2.0;
    private const double DistortionWeight = 16.0;

    // The rate model prices every coefficient on its own and so overstates what a large transform costs once its coefficients are
    // context-coded; scaling its estimate down by this factor (tuned against SSIMULACRA2 on a spread of photographs) removes the bias.
    private const double LargeTransformCostScale = 0.9;

    public static JxlTokenList Encode(JxlVarDctFrameState state, int group)
    {
        var dims = state.Dims;
        int bx0 = group % dims.XSizeGroups * GroupBlocks;
        int by0 = group / dims.XSizeGroups * GroupBlocks;
        int width = Math.Min(GroupBlocks, state.BlocksX - bx0);
        int height = Math.Min(GroupBlocks, state.BlocksY - by0);
        return new Worker(state, bx0, by0, width, height).Run();
    }

    private sealed class Worker
    {
        private readonly JxlVarDctFrameState _state;
        private readonly int _bx0;
        private readonly int _by0;
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _strategy = new byte[GroupBlocks * GroupBlocks];
        private readonly List<(int Cell, int Strategy)> _chosen = [];
        private readonly float[][] _coefficients = [new float[GroupBlocks * GroupBlocks * BlockSize], new float[GroupBlocks * GroupBlocks * BlockSize], new float[GroupBlocks * GroupBlocks * BlockSize]];
        private readonly int[][] _quantized = [new int[GroupBlocks * GroupBlocks * BlockSize], new int[GroupBlocks * GroupBlocks * BlockSize], new int[GroupBlocks * GroupBlocks * BlockSize]];
        private readonly float[] _yDequantized = new float[GroupBlocks * GroupBlocks * BlockSize];
        private readonly float[] _scratch = new float[GroupBlocks * 8 * GroupBlocks * 8 / 4];
        private readonly float[] _costBuffer = new float[32 * 32];
        private readonly int[] _offsets = new int[GroupBlocks * GroupBlocks];
        private readonly int _maxStrategySize;

        public Worker(JxlVarDctFrameState state, int bx0, int by0, int width, int height)
        {
            _state = state;
            _bx0 = bx0;
            _by0 = by0;
            _width = width;
            _height = height;
            int effort = state.Settings.Effort;
            _maxStrategySize = effort >= 7 ? 4 : effort >= 4 ? 2 : 1;
        }

        public JxlTokenList Run()
        {
            Array.Fill(_strategy, Unassigned);
            ChooseStrategies();

            // Blocks in the order the decoder visits them, with their coefficient storage.
            var blocks = new List<(int Lx, int Ly, int Strategy, int Offset, int Raw)>();
            int offset = 0;
            for (int ly = 0; ly < _height; ly++)
            {
                for (int lx = 0; lx < _width; lx++)
                {
                    byte strategy = _strategy[(ly * GroupBlocks) + lx];
                    if (strategy == JxlVarDctFrameState.Covered)
                    {
                        continue;
                    }

                    int covered = AcStrategy.CoveredBlocksX(strategy) * AcStrategy.CoveredBlocksY(strategy);
                    blocks.Add((lx, ly, strategy, offset, RawQuantFor(lx, ly, strategy)));
                    offset += covered * BlockSize;
                }
            }

            var tileStats = new Dictionary<int, CorrelationStats>();
            foreach (var (lx, ly, strategy, off, raw) in blocks)
            {
                TransformBlock(lx, ly, strategy, off, raw, tileStats);
            }

            FitCorrelation(tileStats);

            var tokens = new JxlTokenList();
            var nonZeroCounts = new int[3][];
            for (int c = 0; c < 3; c++)
            {
                nonZeroCounts[c] = new int[GroupBlocks * GroupBlocks];
            }

            foreach (var (lx, ly, strategy, off, raw) in blocks)
            {
                QuantizeChroma(lx, ly, strategy, off, raw);
                foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
                {
                    EmitBlock(tokens, c, lx, ly, strategy, off, raw, nonZeroCounts[c]);
                }
            }

            return tokens;
        }

        // ---- Transform selection ----------------------------------------------------------------------------------------

        private void ChooseStrategies()
        {
            _chosen.Clear();
            if (_maxStrategySize == 1)
            {
                // Only 8x8 blocks are allowed, so there is nothing to compare.
                for (int ly = 0; ly < _height; ly++)
                {
                    for (int lx = 0; lx < _width; lx++)
                    {
                        _chosen.Add(((ly * GroupBlocks) + lx, (int)AcStrategyType.Dct));
                    }
                }
            }
            else
            {
                // The largest transform spans 4x4 cells, so tile the group with such regions.
                for (int ry = 0; ry < _height; ry += 4)
                {
                    for (int rx = 0; rx < _width; rx += 4)
                    {
                        Best(rx, ry, 4, record: true);
                    }
                }
            }

            foreach (var (cell, strategy) in _chosen)
            {
                int lx = cell % GroupBlocks;
                int ly = cell / GroupBlocks;
                int cx = AcStrategy.CoveredBlocksX(strategy);
                int cy = AcStrategy.CoveredBlocksY(strategy);
                for (int y = 0; y < cy; y++)
                {
                    for (int x = 0; x < cx; x++)
                    {
                        _strategy[((ly + y) * GroupBlocks) + lx + x] = JxlVarDctFrameState.Covered;
                    }
                }

                _strategy[cell] = (byte)strategy;
            }
        }

        // The cheapest way to tile the square of s x s cells at (x, y); records the decisions when asked.
        private double Best(int x, int y, int s, bool record)
        {
            if (x >= _width || y >= _height)
            {
                return 0;
            }

            if (s == 1)
            {
                double cost = BlockCost((int)AcStrategyType.Dct, x, y);
                if (record)
                {
                    _chosen.Add(((y * GroupBlocks) + x, (int)AcStrategyType.Dct));
                }

                return cost;
            }

            int h = s / 2;
            double split = Best(x, y, h, false) + Best(x + h, y, h, false) + Best(x, y + h, h, false) + Best(x + h, y + h, h, false);
            double bestCost = split;
            int mode = 0; // 0 split, 1 whole, 2 left/right halves, 3 top/bottom halves

            bool fits = x + s <= _width && y + s <= _height;
            if (fits && s <= _maxStrategySize)
            {
                int whole = s == 2 ? (int)AcStrategyType.Dct16x16 : (int)AcStrategyType.Dct32x32;
                double wholeCost = BlockCost(whole, x, y);
                if (wholeCost < bestCost)
                {
                    bestCost = wholeCost;
                    mode = 1;
                }

                // Two halves: tall ones side by side (width s/2 blocks, height s blocks), or wide ones stacked.
                int tall = s == 2 ? (int)AcStrategyType.Dct16x8 : (int)AcStrategyType.Dct32x16;
                int wide = s == 2 ? (int)AcStrategyType.Dct8x16 : (int)AcStrategyType.Dct16x32;
                double tallCost = BlockCost(tall, x, y) + BlockCost(tall, x + h, y);
                if (tallCost < bestCost)
                {
                    bestCost = tallCost;
                    mode = 2;
                }

                double wideCost = BlockCost(wide, x, y) + BlockCost(wide, x, y + h);
                if (wideCost < bestCost)
                {
                    bestCost = wideCost;
                    mode = 3;
                }
            }

            if (record)
            {
                switch (mode)
                {
                    case 0:
                        Best(x, y, h, true);
                        Best(x + h, y, h, true);
                        Best(x, y + h, h, true);
                        Best(x + h, y + h, h, true);
                        break;
                    case 1:
                        _chosen.Add(((y * GroupBlocks) + x, s == 2 ? (int)AcStrategyType.Dct16x16 : (int)AcStrategyType.Dct32x32));
                        break;
                    case 2:
                    {
                        int tall = s == 2 ? (int)AcStrategyType.Dct16x8 : (int)AcStrategyType.Dct32x16;
                        _chosen.Add(((y * GroupBlocks) + x, tall));
                        _chosen.Add(((y * GroupBlocks) + x + h, tall));
                        break;
                    }

                    default:
                    {
                        int wide = s == 2 ? (int)AcStrategyType.Dct8x16 : (int)AcStrategyType.Dct16x32;
                        _chosen.Add(((y * GroupBlocks) + x, wide));
                        _chosen.Add((((y + h) * GroupBlocks) + x, wide));
                        break;
                    }
                }
            }

            return bestCost;
        }

        // Estimated rate-distortion cost of coding the luma of one block with the given transform.
        private double BlockCost(int strategy, int lx, int ly)
        {
            int cx = AcStrategy.CoveredBlocksX(strategy);
            int cy = AcStrategy.CoveredBlocksY(strategy);
            int covered = cx * cy;
            int size = covered * BlockSize;
            var coefficients = _costBuffer.Length >= size ? _costBuffer.AsSpan(0, size) : new float[size];
            int pixelOffset = (((_by0 + ly) * 8) * _state.Stride) + ((_bx0 + lx) * 8);
            JxlVarDctTransform.Forward(strategy, _state.Planes[1], pixelOffset, _state.Stride, coefficients, _scratch);

            float scale = _state.InvGlobalScale / RawQuantFor(lx, ly, strategy);
            float[] matrix = _state.Matrices[strategy];
            int[] order = AcStrategy.NaturalOrder(strategy);
            float deadzone = _state.Settings.Deadzone;
            double cost = 0;
            for (int k = covered; k < size; k++)
            {
                int pos = order[k];
                float step = matrix[size + pos] * scale;
                float a = MathF.Abs(coefficients[pos]) / step;
                int q = (int)(a + 0.5f - deadzone);
                if (q <= 0)
                {
                    cost += ZeroBits + (DistortionWeight * a * a);
                }
                else
                {
                    float err = a - q;
                    cost += CodedBits + (LogBits * Math.Log2(q)) + (DistortionWeight * err * err);
                }
            }

            return strategy == (int)AcStrategyType.Dct ? cost : cost * LargeTransformCostScale;
        }

        private int RawQuantFor(int lx, int ly, int strategy)
        {
            int cx = AcStrategy.CoveredBlocksX(strategy);
            int cy = AcStrategy.CoveredBlocksY(strategy);
            float sum = 0;
            for (int y = 0; y < cy; y++)
            {
                for (int x = 0; x < cx; x++)
                {
                    sum += _state.AqMultiplier[((_by0 + ly + y) * _state.BlocksX) + _bx0 + lx + x];
                }
            }

            return Math.Clamp((int)MathF.Round(JxlVarDctFrameState.BaseRawQuant * sum / (cx * cy)), 1, 256);
        }

        // ---- Transform, DC and luma ---------------------------------------------------------------------------------------

        private void TransformBlock(int lx, int ly, int strategy, int offset, int raw, Dictionary<int, CorrelationStats> tileStats)
        {
            var state = _state;
            int cx = AcStrategy.CoveredBlocksX(strategy);
            int cy = AcStrategy.CoveredBlocksY(strategy);
            int covered = cx * cy;
            int size = covered * BlockSize;
            int pixelOffset = (((_by0 + ly) * 8) * state.Stride) + ((_bx0 + lx) * 8);
            int[] order = AcStrategy.NaturalOrder(strategy);
            float[] matrix = state.Matrices[strategy];

            // Register the block in the shared frame arrays.
            int firstCell = ((_by0 + ly) * state.BlocksX) + _bx0 + lx;
            state.CellStrategy[firstCell] = (byte)strategy;
            state.RawQuant[firstCell] = raw;
            for (int y = 0; y < cy; y++)
            {
                for (int x = 0; x < cx; x++)
                {
                    if (x != 0 || y != 0)
                    {
                        state.CellStrategy[((_by0 + ly + y) * state.BlocksX) + _bx0 + lx + x] = JxlVarDctFrameState.Covered;
                    }
                }
            }

            for (int c = 0; c < 3; c++)
            {
                JxlVarDctTransform.Forward(strategy, state.Planes[c], pixelOffset, state.Stride, _coefficients[c].AsSpan(offset, size), _scratch);
            }

            // DC: Y directly, X and B relative to the dequantized Y through the frame's DC correlation.
            Span<float> dcX = stackalloc float[covered];
            Span<float> dcY = stackalloc float[covered];
            Span<float> dcB = stackalloc float[covered];
            JxlVarDctTransform.LowestFrequenciesToDc(strategy, _coefficients[0].AsSpan(offset, size), dcX);
            JxlVarDctTransform.LowestFrequenciesToDc(strategy, _coefficients[1].AsSpan(offset, size), dcY);
            JxlVarDctTransform.LowestFrequenciesToDc(strategy, _coefficients[2].AsSpan(offset, size), dcB);
            for (int y = 0; y < cy; y++)
            {
                for (int x = 0; x < cx; x++)
                {
                    int cell = ((_by0 + ly + y) * state.BlocksX) + _bx0 + lx + x;
                    int i = (y * cx) + x;
                    int qy = (int)MathF.Round(dcY[i] / state.DcMultipliers[1]);
                    float yDeq = qy * state.DcMultipliers[1];
                    state.QuantizedDc[1][cell] = qy;
                    state.QuantizedDc[0][cell] = (int)MathF.Round((dcX[i] - (state.DcCorrelation[0] * yDeq)) / state.DcMultipliers[0]);
                    state.QuantizedDc[2][cell] = (int)MathF.Round((dcB[i] - (state.DcCorrelation[2] * yDeq)) / state.DcMultipliers[2]);
                }
            }

            // AC luma, plus the sums chroma-from-luma is fitted from.
            float scaleY = state.InvGlobalScale / raw;
            var q = _quantized[1].AsSpan(offset, size);
            var yDeqAc = _yDequantized.AsSpan(offset, size);
            q.Clear();
            yDeqAc.Clear();
            var y0 = _coefficients[1];
            var x0 = _coefficients[0];
            var b0 = _coefficients[2];
            int tile = ((((_by0 + ly) / ColorCorrelation.TileDimInBlocks) * state.TilesX)) + ((_bx0 + lx) / ColorCorrelation.TileDimInBlocks);
            if (!tileStats.TryGetValue(tile, out var stats))
            {
                stats = new CorrelationStats();
                tileStats[tile] = stats;
            }

            float deadzone = state.Settings.Deadzone;
            for (int k = covered; k < size; k++)
            {
                int pos = order[k];
                float step = matrix[size + pos] * scaleY;
                int value = JxlVarDctQuantization.Quantize(y0[offset + pos] / step, deadzone);
                q[pos] = value;
                float yd = JxlVarDctQuantization.Dequantize(value, state.Biases[1], state.Biases[3]) * step;
                yDeqAc[pos] = yd;
                float xv = x0[offset + pos];
                float bv = b0[offset + pos];
                stats.YY += yd * yd;
                stats.XY += xv * yd;
                stats.BY += bv * yd;
                stats.XX += xv * xv;
                stats.BB += bv * bv;
            }
        }

        // ---- Chroma from luma ---------------------------------------------------------------------------------------------

        private void FitCorrelation(Dictionary<int, CorrelationStats> tileStats)
        {
            foreach (var (tile, s) in tileStats)
            {
                int factorX = 0;
                int factorB = 0;
                if (_state.Settings.ChromaFromLuma && s.YY > 1e-12)
                {
                    // Residual energy of X at ratio r is XX - 2 r XY + r^2 YY; only take a factor that visibly helps.
                    int fx = (int)Math.Clamp(Math.Round(s.XY / s.YY * JxlVarDctConstants.ColorFactor), -128, 127);
                    double rx = (double)fx / JxlVarDctConstants.ColorFactor;
                    double beforeX = s.XX;
                    double afterX = s.XX - (2 * rx * s.XY) + (rx * rx * s.YY);
                    if (afterX < beforeX * 0.97)
                    {
                        factorX = fx;
                    }

                    // B starts from a ratio of one (the default base correlation).
                    int fb = (int)Math.Clamp(Math.Round(((s.BY / s.YY) - 1.0) * JxlVarDctConstants.ColorFactor), -128, 127);
                    double rb = 1.0 + ((double)fb / JxlVarDctConstants.ColorFactor);
                    double beforeB = s.BB - (2 * s.BY) + s.YY;
                    double afterB = s.BB - (2 * rb * s.BY) + (rb * rb * s.YY);
                    if (afterB < beforeB * 0.97)
                    {
                        factorB = fb;
                    }
                }

                _state.YToX[tile] = (sbyte)factorX;
                _state.YToB[tile] = (sbyte)factorB;
            }
        }

        // ---- Chroma quantization and tokens -------------------------------------------------------------------------------

        private void QuantizeChroma(int lx, int ly, int strategy, int offset, int raw)
        {
            var state = _state;
            int covered = AcStrategy.CoveredBlocksX(strategy) * AcStrategy.CoveredBlocksY(strategy);
            int size = covered * BlockSize;
            int[] order = AcStrategy.NaturalOrder(strategy);
            float[] matrix = state.Matrices[strategy];
            int tile = ((((_by0 + ly) / ColorCorrelation.TileDimInBlocks) * state.TilesX)) + ((_bx0 + lx) / ColorCorrelation.TileDimInBlocks);
            float xCc = (float)state.YToX[tile] / JxlVarDctConstants.ColorFactor;
            float bCc = 1f + ((float)state.YToB[tile] / JxlVarDctConstants.ColorFactor);
            float scale = state.InvGlobalScale / raw;
            float scaleX = scale * state.XChannelScale;
            float scaleB = scale * state.BChannelScale;
            float deadzone = state.Settings.Deadzone;
            var qx = _quantized[0].AsSpan(offset, size);
            var qb = _quantized[2].AsSpan(offset, size);
            qx.Clear();
            qb.Clear();
            for (int k = covered; k < size; k++)
            {
                int pos = order[k];
                float yd = _yDequantized[offset + pos];
                qx[pos] = JxlVarDctQuantization.Quantize((_coefficients[0][offset + pos] - (xCc * yd)) / (matrix[pos] * scaleX), deadzone);
                qb[pos] = JxlVarDctQuantization.Quantize((_coefficients[2][offset + pos] - (bCc * yd)) / (matrix[(2 * size) + pos] * scaleB), deadzone);
            }
        }

        private void EmitBlock(JxlTokenList tokens, int channel, int lx, int ly, int strategy, int offset, int raw, int[] nonZeroRow)
        {
            var state = _state;
            int cx = AcStrategy.CoveredBlocksX(strategy);
            int cy = AcStrategy.CoveredBlocksY(strategy);
            int covered = cx * cy;
            int log2Covered = AcStrategy.Log2CoveredBlocks(strategy);
            int size = covered * BlockSize;
            int[] order = AcStrategy.NaturalOrder(strategy);
            var q = _quantized[channel].AsSpan(offset, size);

            int nonZeros = 0;
            for (int k = covered; k < size; k++)
            {
                if (q[order[k]] != 0)
                {
                    nonZeros++;
                }
            }

            int predicted;
            if (lx == 0)
            {
                predicted = ly == 0 ? 32 : nonZeroRow[((ly - 1) * GroupBlocks) + lx];
            }
            else if (ly == 0)
            {
                predicted = nonZeroRow[(ly * GroupBlocks) + lx - 1];
            }
            else
            {
                predicted = (nonZeroRow[((ly - 1) * GroupBlocks) + lx] + nonZeroRow[(ly * GroupBlocks) + lx - 1] + 1) / 2;
            }

            var bctx = state.BlockContext;
            int blockContext = bctx.Context(0, (uint)raw, AcStrategy.OrderOf(strategy), channel);
            tokens.Add(bctx.NonZeroContext(predicted, blockContext), (uint)nonZeros);

            int stored = (nonZeros + covered - 1) >> log2Covered;
            for (int y = 0; y < cy; y++)
            {
                for (int x = 0; x < cx; x++)
                {
                    nonZeroRow[((ly + y) * GroupBlocks) + lx + x] = stored;
                }
            }

            int histogramOffset = bctx.ZeroDensityContextsOffset(blockContext);
            int prev = nonZeros > size / 16 ? 0 : 1;
            int remaining = nonZeros;
            for (int k = covered; k < size && remaining != 0; k++)
            {
                int left = (remaining + covered - 1) >> log2Covered;
                int context = histogramOffset + ((VarDctFrame.KCoeffNumNonzeroContext[left] + VarDctFrame.KCoeffFreqContext[k >> log2Covered]) * 2) + prev;
                uint u = JxlMaTreeEncoder.PackSigned(q[order[k]]);
                tokens.Add(context, u);
                prev = u != 0 ? 1 : 0;
                remaining -= prev;
            }
        }
    }

    private sealed class CorrelationStats
    {
        public double YY;
        public double XY;
        public double BY;
        public double XX;
        public double BB;
    }
}

/// <summary>Scalar quantization shared by the strategy search and the coefficient coder.</summary>
internal static class JxlVarDctQuantization
{
    /// <summary>Rounds <paramref name="scaled"/> (a coefficient in units of its step) to an integer, with a dead zone around zero.</summary>
    public static int Quantize(float scaled, float deadzone)
    {
        float magnitude = MathF.Abs(scaled);
        int q = (int)(magnitude + 0.5f - deadzone);
        if (q < 0)
        {
            q = 0;
        }

        return scaled < 0 ? -q : q;
    }

    /// <summary>The value the decoder reconstructs for a quantized coefficient (see <c>DequantKernel.AdjustQuantBias</c>).</summary>
    public static float Dequantize(int quantized, float channelBias, float bias3)
    {
        if (quantized == 0)
        {
            return 0f;
        }

        float q = quantized;
        return MathF.Abs(q) < 1.125f ? MathF.CopySign(channelBias, q) : q - (bias3 / q);
    }
}
