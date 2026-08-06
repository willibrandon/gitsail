using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GitSail.Git.Execution;

/// <summary>
/// Owns one exact-byte Unix child process, its redirected streams, and its reaping task.
/// </summary>
internal sealed class UnixChildProcess : IDisposable
{
    private const int InterruptedError = 4;
    private const int NoSuchProcessError = 3;

    private UnixChildProcess(
        int processId,
        Stream standardInput,
        Stream standardOutput,
        Stream standardError)
    {
        ProcessId = processId;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
        Completion = Task.Run(() => WaitForExit(processId));
    }

    /// <summary>
    /// Gets the operating-system process identifier.
    /// </summary>
    internal int ProcessId { get; }

    /// <summary>
    /// Gets the parent stream connected to child standard input.
    /// </summary>
    internal Stream StandardInput { get; }

    /// <summary>
    /// Gets the parent stream connected to child standard output.
    /// </summary>
    internal Stream StandardOutput { get; }

    /// <summary>
    /// Gets the parent stream connected to child standard error.
    /// </summary>
    internal Stream StandardError { get; }

    /// <summary>
    /// Gets the task that reaps the child and returns its normalized exit code.
    /// </summary>
    internal Task<int> Completion { get; }

    /// <summary>
    /// Starts one exact native-byte invocation with runtime-owned redirected pipes.
    /// </summary>
    /// <param name="invocation">The complete typed process invocation.</param>
    /// <returns>The owned running child process.</returns>
    internal static UnixChildProcess Start(ProcessInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var processId = Spawn(
            invocation,
            redirectStandardStreams: true,
            out var standardInputFileDescriptor,
            out var standardOutputFileDescriptor,
            out var standardErrorFileDescriptor);
        SafeFileHandle? standardInput = null;
        SafeFileHandle? standardOutput = null;
        SafeFileHandle? standardError = null;
        try
        {
            standardInput = new SafeFileHandle(
                (nint)standardInputFileDescriptor,
                ownsHandle: true);
            standardOutput = new SafeFileHandle(
                (nint)standardOutputFileDescriptor,
                ownsHandle: true);
            standardError = new SafeFileHandle(
                (nint)standardErrorFileDescriptor,
                ownsHandle: true);
            var inputStream = new FileStream(
                standardInput,
                FileAccess.Write,
                bufferSize: 16 * 1024,
                isAsync: false);
            var outputStream = new FileStream(
                standardOutput,
                FileAccess.Read,
                bufferSize: 16 * 1024,
                isAsync: false);
            var errorStream = new FileStream(
                standardError,
                FileAccess.Read,
                bufferSize: 16 * 1024,
                isAsync: false);
            return new UnixChildProcess(processId, inputStream, outputStream, errorStream);
        }
        catch
        {
            standardInput?.Dispose();
            standardOutput?.Dispose();
            standardError?.Dispose();
            _ = UnixNative.Kill(processId, signal: 9);
            try
            {
                _ = WaitForExit(processId);
            }
            catch (Win32Exception)
            {
            }

            throw;
        }
    }

    /// <summary>
    /// Starts one exact native-byte invocation attached to the parent's terminal streams.
    /// </summary>
    /// <param name="invocation">The complete typed process invocation.</param>
    /// <returns>The owned running child process with no parent-side pipe streams.</returns>
    internal static UnixChildProcess StartAttached(ProcessInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var processId = Spawn(
            invocation,
            redirectStandardStreams: false,
            out _,
            out _,
            out _);
        return new UnixChildProcess(processId, Stream.Null, Stream.Null, Stream.Null);
    }

    /// <summary>
    /// Sends one native signal to this child process.
    /// </summary>
    /// <param name="signal">The native Unix signal number.</param>
    /// <returns><see langword="true"/> when delivered or the process has already exited.</returns>
    internal bool TrySignal(int signal)
    {
        if (UnixNative.Kill(ProcessId, signal) == 0)
        {
            return true;
        }

        return Marshal.GetLastPInvokeError() == NoSuchProcessError;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
    }

    private static unsafe int Spawn(
        ProcessInvocation invocation,
        bool redirectStandardStreams,
        out int standardInputFileDescriptor,
        out int standardOutputFileDescriptor,
        out int standardErrorFileDescriptor)
    {
        var filename = ToNullTerminated(ProcessArgument.Literal(invocation.Executable.Path).GetUnixBytes());
        var workingDirectory = ToNullTerminated(invocation.WorkingDirectory.GetUnixBytes());
        var argumentValues = new byte[invocation.Arguments.Length + 1][];
        argumentValues[0] = invocation.Executable.Kind == ProgramKind.Shell
            ? "/bin/sh"u8.ToArray()
            : filename[..^1];
        for (var index = 0; index < invocation.Arguments.Length; index++)
        {
            argumentValues[index + 1] = invocation.Arguments[index].GetUnixBytes().ToArray();
        }

        using var argumentVector = NativeStringArray.Create(argumentValues);
        using var environmentVector = NativeStringArray.Create(invocation.Environment.GetUnixEntries());
        fixed (byte* filenamePointer = filename)
        fixed (byte* workingDirectoryPointer = workingDirectory)
        {
            var childProcessId = -1;
            var inputFileDescriptor = -1;
            var outputFileDescriptor = -1;
            var errorFileDescriptor = -1;
            var result = UnixNative.ForkAndExecProcess(
                filenamePointer,
                argumentVector.Pointer,
                environmentVector.Pointer,
                workingDirectoryPointer,
                redirectStandardStreams ? 1 : 0,
                redirectStandardStreams ? 1 : 0,
                redirectStandardStreams ? 1 : 0,
                0,
                0,
                0,
                null,
                0,
                &childProcessId,
                &inputFileDescriptor,
                &outputFileDescriptor,
                &errorFileDescriptor);
            if (result != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(error, "The Unix child process could not be started.");
            }

            if (childProcessId <= 0)
            {
                throw new InvalidOperationException("The Unix process boundary returned an invalid process identifier.");
            }

            standardInputFileDescriptor = inputFileDescriptor;
            standardOutputFileDescriptor = outputFileDescriptor;
            standardErrorFileDescriptor = errorFileDescriptor;
            return childProcessId;
        }
    }

    private static unsafe int WaitForExit(int processId)
    {
        while (true)
        {
            var status = 0;
            var result = UnixNative.WaitProcess(processId, &status, options: 0);
            if (result == processId)
            {
                var terminatingSignal = status & 0x7f;
                return terminatingSignal == 0
                    ? (status >> 8) & 0xff
                    : 128 + terminatingSignal;
            }

            var error = Marshal.GetLastPInvokeError();
            if (result < 0 && error == InterruptedError)
            {
                continue;
            }

            throw new Win32Exception(error, "The Unix child process could not be reaped.");
        }
    }

    private static byte[] ToNullTerminated(ReadOnlySpan<byte> value)
    {
        var terminated = new byte[value.Length + 1];
        value.CopyTo(terminated);
        return terminated;
    }
}
