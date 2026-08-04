using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Presents one repository status entry in a staged or unstaged file pane.
/// </summary>
internal sealed record StatusWorkspaceItem
{
    /// <summary>
    /// Initializes a status-pane item from one exact Git status entry.
    /// </summary>
    /// <param name="entry">The lossless Git status entry.</param>
    /// <param name="status">The status represented by this pane item.</param>
    internal StatusWorkspaceItem(RepositoryStatusEntry entry, GitFileStatus status)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
        Status = status;
    }

    /// <summary>
    /// Gets the lossless Git status entry backing this item.
    /// </summary>
    internal RepositoryStatusEntry Entry { get; }

    /// <summary>
    /// Gets the exact path used as the stable item and selection identity.
    /// </summary>
    internal GitPath Path => Entry.Path;

    /// <summary>
    /// Gets the index-side or worktree-side status represented by this item.
    /// </summary>
    internal GitFileStatus Status { get; }

    /// <summary>
    /// Returns the control-safe, human-readable row text shown in the file pane.
    /// </summary>
    /// <returns>The status marker followed by the escaped path display.</returns>
    public override string ToString()
    {
        var path = Entry.OriginalPath is null
            ? Entry.Path.DisplayText
            : $"{Entry.OriginalPath.DisplayText} -> {Entry.Path.DisplayText}";
        var submodule = Entry.IsSubmodule ? " [submodule]" : string.Empty;
        return $"{GetStatusMarker(Status)} {path}{submodule}";
    }

    private static char GetStatusMarker(GitFileStatus status)
        => status switch
        {
            GitFileStatus.Unmodified => '.',
            GitFileStatus.Modified => 'M',
            GitFileStatus.Added => 'A',
            GitFileStatus.Deleted => 'D',
            GitFileStatus.Renamed => 'R',
            GitFileStatus.Copied => 'C',
            GitFileStatus.TypeChanged => 'T',
            GitFileStatus.Unmerged => 'U',
            GitFileStatus.Untracked => '?',
            GitFileStatus.Ignored => '!',
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
}
