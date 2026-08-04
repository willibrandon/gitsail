using GitSail.Domain;
using System.Globalization;

namespace GitSail.Ui;

/// <summary>
/// Presents one exact stash reflog entry in the searchable stash window.
/// </summary>
internal sealed class StashWorkspaceItem
{
    /// <summary>
    /// Initializes one display item over an exact stash entry.
    /// </summary>
    /// <param name="stash">The exact stash entry captured from Git.</param>
    internal StashWorkspaceItem(StashInfo stash)
    {
        ArgumentNullException.ThrowIfNull(stash);
        Stash = stash;
        Key = new StashIdentity(stash.Index, stash.ObjectId);
    }

    /// <summary>
    /// Gets the exact stash entry backing this item.
    /// </summary>
    internal StashInfo Stash { get; }

    /// <summary>
    /// Gets the exact object and current reflog position used as list identity.
    /// </summary>
    internal StashIdentity Key { get; }

    /// <summary>
    /// Returns one compact control-safe stash row with selector, time, object, and subject.
    /// </summary>
    /// <returns>The human-readable stash-list row.</returns>
    public override string ToString()
    {
        var localTime = Stash.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        return $"{Stash.Selector,-11} {localTime}  {Stash.ObjectId.ToString()[..12]}  {Stash.DisplayMessage}";
    }
}
