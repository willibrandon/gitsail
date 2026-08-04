namespace GitSail.Git.Execution;

/// <summary>
/// Contains the exit status and exact bounded output bytes from one child process.
/// </summary>
/// <param name="ExitCode">The native child-process exit code.</param>
/// <param name="StandardOutput">The exact retained standard-output bytes.</param>
/// <param name="StandardError">The exact retained standard-error bytes.</param>
/// <param name="Duration">The elapsed child-process duration.</param>
/// <param name="StandardOutputSpool">The owned exact output spool when spooling was requested.</param>
internal sealed record ProcessResult(
    int ExitCode,
    ReadOnlyMemory<byte> StandardOutput,
    ReadOnlyMemory<byte> StandardError,
    TimeSpan Duration,
    RawByteSpool? StandardOutputSpool = null);
