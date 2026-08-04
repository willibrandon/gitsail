using System.Diagnostics;

namespace GitSail.Git.Execution;

/// <summary>
/// Runs a trusted typed child with the terminal attached after the GitSail TUI has stopped.
/// </summary>
internal sealed class TerminalChildProcessRunner : ITerminalChildProcessRunner
{
    /// <summary>
    /// Runs one invocation with inherited standard streams and returns its exit status.
    /// </summary>
    /// <param name="invocation">The complete child-process contract.</param>
    /// <param name="cancellationToken">Signals cancellation and child-tree termination.</param>
    /// <returns>The normalized child exit status.</returns>
    public async Task<int> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!invocation.StandardInput.GetBytes().IsEmpty)
        {
            throw new ArgumentException(
                "A terminal-attached invocation cannot supply redirected standard input.",
                nameof(invocation));
        }

        if (!ExecutableResolver.IsUnchanged(invocation.Executable))
        {
            throw new InvalidOperationException("The resolved executable changed before launch.");
        }

        return OperatingSystem.IsWindows()
            ? await RunWindowsAsync(invocation, cancellationToken).ConfigureAwait(false)
            : await RunUnixAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunWindowsAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateWindowsStartInfo(invocation),
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("The terminal child process could not be started.");
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return process.ExitCode;
    }

    private static async Task<int> RunUnixAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        using var process = UnixChildProcess.StartAttached(invocation);
        try
        {
            return await process.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelUnixProcessAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    private static ProcessStartInfo CreateWindowsStartInfo(ProcessInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.Executable.Path,
            WorkingDirectory = invocation.WorkingDirectory.GetWindowsPath(),
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument.GetWindowsValue());
        }

        startInfo.Environment.Clear();
        invocation.Environment.CopyTo(startInfo.Environment);
        return startInfo;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task CancelUnixProcessAsync(UnixChildProcess process)
    {
        const int interruptSignal = 2;
        const int terminateSignal = 15;
        const int killSignal = 9;
        var gracePeriod = TimeSpan.FromSeconds(2);

        _ = process.TrySignal(interruptSignal);
        if (await CompletesWithinAsync(process.Completion, gracePeriod).ConfigureAwait(false))
        {
            _ = await process.Completion.ConfigureAwait(false);
            return;
        }

        _ = process.TrySignal(terminateSignal);
        if (await CompletesWithinAsync(process.Completion, gracePeriod).ConfigureAwait(false))
        {
            _ = await process.Completion.ConfigureAwait(false);
            return;
        }

        _ = process.TrySignal(killSignal);
        _ = await process.Completion.ConfigureAwait(false);
    }

    private static async Task<bool> CompletesWithinAsync(Task<int> completion, TimeSpan timeout)
        => ReferenceEquals(
            await Task.WhenAny(completion, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false),
            completion);
}
