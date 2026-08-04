using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Presents one exact file patch in the comparison workspace list.
/// </summary>
internal sealed class DiffWorkspaceItem
{
    /// <summary>
    /// Initializes a comparison row over one indexed raw file patch.
    /// </summary>
    /// <param name="file">The exact indexed file patch.</param>
    internal DiffWorkspaceItem(RawDiffFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        File = file;
    }

    /// <summary>
    /// Gets the exact raw file patch represented by this row.
    /// </summary>
    internal RawDiffFile File { get; }

    /// <summary>
    /// Returns a compact path, rename, and binary-status description.
    /// </summary>
    /// <returns>The control-safe comparison row.</returns>
    public override string ToString()
    {
        var oldPath = TerminalTextSanitizer.Sanitize(File.OldPath.DisplayText);
        var newPath = TerminalTextSanitizer.Sanitize(File.NewPath.DisplayText);
        var path = File.OldPath.Equals(File.NewPath)
            ? newPath
            : $"{oldPath} → {newPath}";
        return File.IsBinary ? $"[binary] {path}" : path;
    }
}
