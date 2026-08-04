using GitSail.Domain;
using System.Globalization;

namespace GitSail.Ui;

/// <summary>
/// Presents one exact Git tree entry in the repository browser.
/// </summary>
internal sealed class TreeWorkspaceItem
{
    /// <summary>
    /// Initializes one display item over an exact tree entry.
    /// </summary>
    /// <param name="entry">The exact immutable tree entry.</param>
    internal TreeWorkspaceItem(TreeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
    }

    /// <summary>
    /// Gets the exact immutable tree entry backing this item.
    /// </summary>
    internal TreeEntry Entry { get; }

    /// <summary>
    /// Returns one compact control-safe tree row with kind, name, mode, and size.
    /// </summary>
    /// <returns>The human-readable tree row.</returns>
    public override string ToString()
    {
        var kind = Entry.Kind switch
        {
            TreeEntryKind.Tree => "DIR ",
            TreeEntryKind.RegularFile => "FILE",
            TreeEntryKind.ExecutableFile => "EXEC",
            TreeEntryKind.SymbolicLink => "LINK",
            TreeEntryKind.GitLink => "SUBM",
            _ => throw new ArgumentOutOfRangeException(),
        };
        var size = Entry.Size is null
            ? string.Empty
            : Entry.Size.Value.ToString("N0", CultureInfo.CurrentCulture) + " bytes";
        return $"{kind}  {Entry.Name.DisplayText}  {Entry.Mode}  {size}".TrimEnd();
    }
}
