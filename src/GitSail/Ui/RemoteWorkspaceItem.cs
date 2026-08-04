using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Adapts one exact remote record for stable keyed terminal-list presentation.
/// </summary>
internal sealed class RemoteWorkspaceItem
{
    /// <summary>
    /// Initializes one remote list item from an exact catalog record.
    /// </summary>
    /// <param name="remote">The exact remote configuration record.</param>
    internal RemoteWorkspaceItem(RemoteInfo remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        Remote = remote;
    }

    /// <summary>
    /// Gets the exact remote record represented by this row.
    /// </summary>
    internal RemoteInfo Remote { get; }

    /// <summary>
    /// Gets the exact remote name used as the stable list key.
    /// </summary>
    internal RemoteName Key => Remote.Name;

    /// <inheritdoc />
    public override string ToString()
    {
        var fetch = Remote.FetchUrls.IsEmpty
            ? "<no fetch URL>"
            : Remote.FetchUrls[0].RedactedDisplayText;
        return $"{Remote.Name.DisplayText} | {fetch}";
    }
}
