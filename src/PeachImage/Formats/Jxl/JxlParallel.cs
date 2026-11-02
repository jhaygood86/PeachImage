using System.Runtime.ExceptionServices;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl;

/// <summary>Runs independent work items (frame groups) in parallel within the caller's <see cref="ParallelSettings"/>, rethrowing the first failure as itself rather than as an aggregate.</summary>
internal static class JxlParallel
{
    public static void For(int count, Action<int> body, ParallelSettings settings = default)
    {
        if (count <= 1 || settings.IsSequential || Environment.ProcessorCount == 1)
        {
            for (int i = 0; i < count; i++)
            {
                body(i);
            }

            return;
        }

        try
        {
            Parallel.For(0, count, settings.ToParallelOptions(), (i, state) =>
            {
                if (!state.IsExceptional)
                {
                    body(i);
                }
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
    }

    /// <summary>
    /// A plain <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/> over <paramref name="count"/> items that propagates
    /// failures as an <see cref="AggregateException"/> (callers that translate exceptions rely on that); sequential when asked.
    /// </summary>
    public static void Rows(int count, ParallelSettings settings, Action<int> body)
    {
        if (settings.IsSequential)
        {
            for (int i = 0; i < count; i++)
            {
                body(i);
            }

            return;
        }

        Parallel.For(0, count, settings.ToParallelOptions(), body);
    }
}
