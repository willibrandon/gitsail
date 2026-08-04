using GitSail.CommandLine;

namespace GitSail.Ui;

/// <summary>
/// Contains the process inputs needed to start an interactive GitSail shell.
/// </summary>
/// <param name="Mode">The selected top-level workflow.</param>
/// <param name="WorkingDirectory">The requested working directory, or <see langword="null"/> for the process directory.</param>
/// <param name="Citool">The single-transaction options, or <see langword="null"/> outside citool mode.</param>
/// <param name="History">The structured history operands, or <see langword="null"/> outside history mode.</param>
/// <param name="Browser">The repository tree operands, or <see langword="null"/> outside browser mode.</param>
/// <param name="Blame">The line-history operands, or <see langword="null"/> outside blame mode.</param>
/// <param name="Diff">The comparison operands, or <see langword="null"/> outside diff mode.</param>
internal sealed record GitSailShellOptions(
    ApplicationMode Mode,
    string? WorkingDirectory,
    CitoolOptions? Citool = null,
    HistoryOptions? History = null,
    BrowserOptions? Browser = null,
    BlameOptions? Blame = null,
    DiffOptions? Diff = null);
