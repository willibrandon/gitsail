namespace GitSail.Git.Execution;

/// <summary>
/// Represents a canonical executable selected from a sanitized search path.
/// </summary>
/// <param name="Kind">The trusted executable family.</param>
/// <param name="Path">The canonical absolute executable path.</param>
/// <param name="Fingerprint">The file fingerprint captured during resolution.</param>
internal sealed record ResolvedExecutable(
    ProgramKind Kind,
    string Path,
    ExecutableFingerprint Fingerprint);
