namespace GitSail.CommandLine;

/// <summary>
/// Identifies the top-level workflow selected by System.CommandLine.
/// </summary>
internal enum ApplicationMode
{
    /// <summary>
    /// Opens the main commit workspace.
    /// </summary>
    Gui,

    /// <summary>
    /// Runs the single-commit workflow.
    /// </summary>
    Citool,

    /// <summary>
    /// Opens incremental blame.
    /// </summary>
    Blame,

    /// <summary>
    /// Opens the repository tree browser.
    /// </summary>
    Browser,

    /// <summary>
    /// Opens the comparison workspace.
    /// </summary>
    Diff,

    /// <summary>
    /// Opens conflict resolution.
    /// </summary>
    Merge,

    /// <summary>
    /// Opens structured history.
    /// </summary>
    History,

    /// <summary>
    /// Opens interactive rebase planning or recovery.
    /// </summary>
    Rebase,

    /// <summary>
    /// Opens the repository chooser.
    /// </summary>
    Pick,

    /// <summary>
    /// Writes installation and runtime diagnostics.
    /// </summary>
    Doctor,

    /// <summary>
    /// Writes command help.
    /// </summary>
    Help,

    /// <summary>
    /// Generates shell completions.
    /// </summary>
    Completion,

    /// <summary>
    /// Writes the application version.
    /// </summary>
    Version,
}
