namespace GitSail.Git.Execution;

/// <summary>
/// Contains exact output bytes from a successful typed Git operation.
/// </summary>
/// <param name="StandardOutput">The exact standard-output bytes.</param>
/// <param name="StandardError">The exact standard-error warning or progress bytes.</param>
internal sealed record GitOperationResult(
    ReadOnlyMemory<byte> StandardOutput,
    ReadOnlyMemory<byte> StandardError);
