namespace GitSail;

/// <summary>
/// Provides the native application entry point.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await ApplicationHost.RunAsync(args, cancellationSource.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
