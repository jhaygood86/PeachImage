using PeachImage.Internal.Icc;

namespace PeachImage.Internal.PixelFormatConversion;

/// <summary>
/// ICC-aware device→device conversion for any pair of device color spaces PeachImage's ICC engine supports:
/// routes both profiles' transforms through the shared D50 PCS they each already resolve to internally
/// (<see cref="IccProfile.ToXyzD50"/> / <see cref="IccProfile.FromXyzD50"/>), so "RGB device values into a
/// CMYK profile" and "CMYK profile A into CMYK profile B" are the same operation, just with different
/// source/destination profiles. Unlike <see cref="IccDeviceToSrgbConverter"/>'s SIMD-batched sink stage (a
/// single fixed matrix, since sRGB is always the destination), the destination here is arbitrary and its own
/// PCS→device transform is just as data-dependent per pixel as the source's device→PCS transform, so both
/// stages run scalar throughout. Also the engine behind the public <see cref="PeachImage.IccColorProfile.ConvertTo"/> API.
/// </summary>
internal static class IccDeviceToDeviceConverter
{
    internal static void Convert(
        ReadOnlySpan<byte> deviceValues,
        Span<byte> destinationValues,
        int pixelCount,
        IccProfile source,
        IccProfile destination,
        IccIntent intent,
        bool blackPointCompensation)
    {
        int sourceChannels = source.ChannelCount;
        int destinationChannels = destination.ChannelCount;

        bool applyBpc = blackPointCompensation && IccBlackPointCompensation.AppliesTo(intent);
        IccVector3 sourceBlack = default;
        IccVector3 destinationBlack = default;
        if (applyBpc)
        {
            sourceBlack = source.GetBlackPointXyzD50(intent);
            destinationBlack = destination.GetBlackPointXyzD50(intent);
        }

        Span<double> normalizedDevice = stackalloc double[sourceChannels];
        Span<double> destinationDevice = stackalloc double[destinationChannels];

        for (int i = 0; i < pixelCount; i++)
        {
            int sourceOffset = i * sourceChannels;
            for (int c = 0; c < sourceChannels; c++)
            {
                normalizedDevice[c] = deviceValues[sourceOffset + c] / 255.0;
            }

            var xyz = source.ToXyzD50(normalizedDevice, intent);
            if (applyBpc)
            {
                xyz = IccBlackPointCompensation.Apply(xyz, sourceBlack, destinationBlack);
            }

            destination.FromXyzD50(xyz, intent, destinationDevice);

            int destinationOffset = i * destinationChannels;
            for (int c = 0; c < destinationChannels; c++)
            {
                destinationValues[destinationOffset + c] = IccColorMath.NormalizedToByte(destinationDevice[c]);
            }
        }
    }
}
