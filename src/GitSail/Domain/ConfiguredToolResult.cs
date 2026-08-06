namespace GitSail.Domain;

/// <summary>
/// Contains the bounded exact result of one reviewed configured-tool request.
/// </summary>
/// <param name="Outcome">The denied, successful, or failed disposition.</param>
/// <param name="ExitCode">The child exit status, or none when denied before launch.</param>
/// <param name="StandardOutput">The exact bounded standard-output bytes.</param>
/// <param name="StandardError">The exact bounded standard-error bytes.</param>
/// <param name="Duration">The measured child duration, or zero when denied.</param>
internal sealed record ConfiguredToolResult(
    ConfiguredToolOutcome Outcome,
    int? ExitCode,
    ReadOnlyMemory<byte> StandardOutput,
    ReadOnlyMemory<byte> StandardError,
    TimeSpan Duration);
