using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Presents one exact branch record in the searchable branch window.
/// </summary>
internal sealed class BranchWorkspaceItem
{
    /// <summary>
    /// Initializes one display item over an exact branch record.
    /// </summary>
    /// <param name="branch">The exact branch record captured from Git.</param>
    internal BranchWorkspaceItem(BranchInfo branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        Branch = branch;
    }

    /// <summary>
    /// Gets the exact branch record backing this item.
    /// </summary>
    internal BranchInfo Branch { get; }

    /// <summary>
    /// Gets the complete exact ref used as the stable list identity.
    /// </summary>
    internal RefName Key => Branch.FullName;

    /// <summary>
    /// Returns a compact control-safe branch row with state, tracking, and occupancy cues.
    /// </summary>
    /// <returns>The human-readable branch-list row.</returns>
    public override string ToString()
    {
        var current = Branch.IsCurrent ? "*" : " ";
        var kind = Branch.Kind == BranchKind.Local ? "local " : "remote";
        var symbolic = Branch.SymbolicTarget is null
            ? string.Empty
            : $" -> {Branch.SymbolicTarget.DisplayText}";
        var tracking = GetTrackingText();
        var occupancy = Branch.OccupiedWorktrees.IsEmpty
            ? string.Empty
            : $" [worktrees: {Branch.OccupiedWorktrees.Length}]";
        return $"{current} {kind}  {Branch.ShortName.DisplayText}{symbolic}{tracking}{occupancy}";
    }

    private string GetTrackingText()
    {
        if (Branch.IsUpstreamGone)
        {
            return " [upstream gone]";
        }

        if (Branch.UpstreamName is null)
        {
            return string.Empty;
        }

        return $" [ahead {Branch.AheadCount}, behind {Branch.BehindCount}]";
    }
}
