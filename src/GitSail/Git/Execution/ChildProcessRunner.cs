using System.Buffers;
using System.Diagnostics;

namespace GitSail.Git.Execution;

/// <summary>
/// Executes typed child invocations without a shell and drains both byte streams concurrently.
/// </summary>
internal sealed class ChildProcessRunner : IChildProcessRunner
{
    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!ExecutableResolver.IsUnchanged(invocation.Executable))
        {
            throw new InvalidOperationException("The resolved executable changed before launch.");
        }

        return OperatingSystem.IsWindows()
            ? await RunWindowsAsync(invocation, cancellationToken).ConfigureAwait(false)
            : await RunUnixAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProcessResult> RunWindowsAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var process = StartWindowsProcess(invocation);
        var standardOutputTask = ReadBoundedAsync(
            process.StandardOutput.BaseStream,
            invocation.OutputPolicy.MaximumStandardOutputBytes,
            invocation.OutputPolicy.StandardOutputSpoolMemoryThresholdBytes,
            cancellationToken);
        var standardErrorTask = ReadBoundedAsync(
            process.StandardError.BaseStream,
            invocation.OutputPolicy.MaximumStandardErrorBytes,
            spoolMemoryThresholdBytes: 0,
            cancellationToken);
        var standardInputTask = WriteStandardInputAsync(
            process.StandardInput.BaseStream,
            invocation.StandardInput.GetBytes(),
            cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await standardInputTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await DrainAfterCancellationAsync(
                standardOutputTask,
                standardErrorTask,
                standardInputTask).ConfigureAwait(false);
            throw;
        }

        return await CreateResultAsync(
            process.ExitCode,
            startedAt,
            invocation.OutputPolicy,
            standardOutputTask,
            standardErrorTask).ConfigureAwait(false);
    }

    private static async Task<ProcessResult> RunUnixAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var process = UnixChildProcess.Start(invocation);
        var standardOutputTask = ReadBoundedAsync(
            process.StandardOutput,
            invocation.OutputPolicy.MaximumStandardOutputBytes,
            invocation.OutputPolicy.StandardOutputSpoolMemoryThresholdBytes,
            cancellationToken);
        var standardErrorTask = ReadBoundedAsync(
            process.StandardError,
            invocation.OutputPolicy.MaximumStandardErrorBytes,
            spoolMemoryThresholdBytes: 0,
            cancellationToken);
        var standardInputTask = WriteStandardInputAsync(
            process.StandardInput,
            invocation.StandardInput.GetBytes(),
            cancellationToken);

        int exitCode;
        try
        {
            exitCode = await process.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            await standardInputTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelUnixProcessAsync(process).ConfigureAwait(false);
            await DrainAfterCancellationAsync(
                standardOutputTask,
                standardErrorTask,
                standardInputTask).ConfigureAwait(false);
            throw;
        }

        return await CreateResultAsync(
            exitCode,
            startedAt,
            invocation.OutputPolicy,
            standardOutputTask,
            standardErrorTask).ConfigureAwait(false);
    }

    private static Process StartWindowsProcess(ProcessInvocation invocation)
    {
        var process = new Process
        {
            StartInfo = CreateWindowsStartInfo(invocation),
        };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The child process could not be started.");
            }

            return process;
        }
        catch
        {
            process.Dispose();
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
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument.GetWindowsValue());
        }

        startInfo.Environment.Clear();
        invocation.Environment.CopyTo(startInfo.Environment);
        return startInfo;
    }

    private static async Task<ProcessResult> CreateResultAsync(
        int exitCode,
        long startedAt,
        OutputPolicy outputPolicy,
        Task<(byte[] Bytes, RawByteSpool? Spool, bool LimitExceeded)> standardOutputTask,
        Task<(byte[] Bytes, RawByteSpool? Spool, bool LimitExceeded)> standardErrorTask)
    {
        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        if (standardOutput.LimitExceeded)
        {
            standardOutput.Spool?.Dispose();
            throw new ProcessOutputLimitExceededException(
                "standard output",
                outputPolicy.MaximumStandardOutputBytes);
        }

        if (standardError.LimitExceeded)
        {
            standardOutput.Spool?.Dispose();
            throw new ProcessOutputLimitExceededException(
                "standard error",
                outputPolicy.MaximumStandardErrorBytes);
        }

        return new ProcessResult(
            exitCode,
            standardOutput.Bytes,
            standardError.Bytes,
            Stopwatch.GetElapsedTime(startedAt),
            standardOutput.Spool);
    }

    private static async Task<(byte[] Bytes, RawByteSpool? Spool, bool LimitExceeded)> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        int spoolMemoryThresholdBytes,
        CancellationToken cancellationToken)
    {
        var writer = spoolMemoryThresholdBytes == 0
            ? new ArrayBufferWriter<byte>(Math.Min(maximumBytes, 16 * 1024))
            : null;
        var spool = spoolMemoryThresholdBytes == 0
            ? null
            : RawByteSpool.Create(spoolMemoryThresholdBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var limitExceeded = false;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var retainedCount = spool?.Length ?? writer!.WrittenCount;
                var remaining = maximumBytes - checked((int)retainedCount);
                if (remaining <= 0)
                {
                    limitExceeded = true;
                    continue;
                }

                var retained = Math.Min(read, remaining);
                if (spool is null)
                {
                    writer!.Write(buffer.AsSpan(0, retained));
                }
                else
                {
                    await spool.AppendAsync(buffer.AsMemory(0, retained), cancellationToken).ConfigureAwait(false);
                }

                limitExceeded |= retained != read;
            }

            return (writer?.WrittenSpan.ToArray() ?? [], spool, limitExceeded);
        }
        catch
        {
            spool?.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteStandardInputAsync(
        Stream stream,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!bytes.IsEmpty)
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
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
        const int InterruptSignal = 2;
        const int TerminateSignal = 15;
        const int KillSignal = 9;
        var gracePeriod = TimeSpan.FromSeconds(2);

        _ = process.TrySignal(InterruptSignal);
        if (await CompletesWithinAsync(process.Completion, gracePeriod).ConfigureAwait(false))
        {
            _ = await process.Completion.ConfigureAwait(false);
            return;
        }

        _ = process.TrySignal(TerminateSignal);
        if (await CompletesWithinAsync(process.Completion, gracePeriod).ConfigureAwait(false))
        {
            _ = await process.Completion.ConfigureAwait(false);
            return;
        }

        _ = process.TrySignal(KillSignal);
        _ = await process.Completion.ConfigureAwait(false);
    }

    private static async Task<bool> CompletesWithinAsync(Task<int> completion, TimeSpan timeout)
        => ReferenceEquals(
            await Task.WhenAny(completion, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false),
            completion);

    private static async Task DrainAfterCancellationAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }
}
