namespace GitSail.Domain;

/// <summary>
/// Selects which untracked classes a stash-create transaction includes.
/// </summary>
internal enum StashFileScope
{
    /// <summary>
    /// Includes tracked index and worktree changes only.
    /// </summary>
    Tracked,

    /// <summary>
    /// Includes tracked changes and nonignored untracked paths.
    /// </summary>
    IncludeUntracked,

    /// <summary>
    /// Includes tracked changes plus untracked and ignored paths.
    /// </summary>
    IncludeIgnored,
}
