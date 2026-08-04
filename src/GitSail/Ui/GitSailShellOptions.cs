using GitSail.CommandLine;

namespace GitSail.Ui;

/// <summary>
/// Contains the process inputs needed to start an interactive GitSail shell.
/// </summary>
/// <param name="Mode">The selected top-level workflow.</param>
/// <param name="WorkingDirectory">The requested working directory, or <see langword="null"/> for the process directory.</param>
internal sealed record GitSailShellOptions(
    ApplicationMode Mode,
    string? WorkingDirectory);
