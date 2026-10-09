using System.Runtime.ExceptionServices;

namespace PeachImage.Formats.Jxl;

/// <summary>Runs independent work items (frame groups) on all cores, rethrowing the first failure as itself rather than as an aggregate.</summary>
internal static class JxlParallel
{
    private static readonly ParallelOptions Options = new() { MaxDegreeOfParallelism = Environment.ProcessorCount };

    public static void For(int count, Action<int> body)
    {
        if (count <= 1 || Environment.ProcessorCount == 1)
        {
            for (int i = 0; i < count; i++)
            {
                body(i);
            }

            return;
        }

        try
        {
            Parallel.For(0, count, Options, (i, state) =>
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
}
