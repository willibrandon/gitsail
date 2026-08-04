namespace GitSail.Git.Execution;

/// <summary>
/// Contains the resolved Git executable and its parsed version.
/// </summary>
/// <param name="Executable">The canonical trusted Git executable.</param>
/// <param name="Version">The version reported by that executable.</param>
internal sealed record GitInstallation(
    ResolvedExecutable Executable,
    GitVersion Version);
