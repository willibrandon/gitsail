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

        using var process = new Process
        {
            StartInfo = CreateStartInfo(invocation),
        };
        var startedAt = Stopwatch.GetTimestamp();
        if (!process.Start())
        {
            throw new InvalidOperationException("The child process could not be started.");
        }

        var standardOutputTask = ReadBoundedAsync(
            process.StandardOutput.BaseStream,
            invocation.OutputPolicy.MaximumStandardOutputBytes,
            cancellationToken);
        var standardErrorTask = ReadBoundedAsync(
            process.StandardError.BaseStream,
            invocation.OutputPolicy.MaximumStandardErrorBytes,
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
            await DrainAfterCancellationAsync(standardOutputTask, standardErrorTask, standardInputTask).ConfigureAwait(false);
            throw;
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        if (standardOutput.LimitExceeded)
        {
            throw new ProcessOutputLimitExceededException(
                "standard output",
                invocation.OutputPolicy.MaximumStandardOutputBytes);
        }

        if (standardError.LimitExceeded)
        {
            throw new ProcessOutputLimitExceededException(
                "standard error",
                invocation.OutputPolicy.MaximumStandardErrorBytes);
        }

        return new ProcessResult(
            process.ExitCode,
            standardOutput.Bytes,
            standardError.Bytes,
            Stopwatch.GetElapsedTime(startedAt));
    }

    private static ProcessStartInfo CreateStartInfo(ProcessInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.Executable.Path,
            WorkingDirectory = invocation.WorkingDirectory.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument.Value);
        }

        startInfo.Environment.Clear();
        invocation.Environment.CopyTo(startInfo.Environment);
        return startInfo;
    }

    private static async Task<(byte[] Bytes, bool LimitExceeded)> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>(Math.Min(maximumBytes, 16 * 1024));
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

                var remaining = maximumBytes - writer.WrittenCount;
                if (remaining <= 0)
                {
                    limitExceeded = true;
                    continue;
                }

                var retained = Math.Min(read, remaining);
                writer.Write(buffer.AsSpan(0, retained));
                limitExceeded |= retained != read;
            }

            return (writer.WrittenSpan.ToArray(), limitExceeded);
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
