using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Represents one indexed configured push URL in the remote-initialization selector.
/// </summary>
internal sealed class RemoteInitializationUrlItem
{
    /// <summary>
    /// Initializes one selector item without losing duplicate configured URL positions.
    /// </summary>
    /// <param name="index">The zero-based configured push-URL index.</param>
    /// <param name="url">The exact configured push URL.</param>
    internal RemoteInitializationUrlItem(int index, RemoteUrl url)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(url);
        Index = index;
        Url = url;
    }

    /// <summary>
    /// Gets the zero-based configured push-URL index.
    /// </summary>
    internal int Index { get; }

    /// <summary>
    /// Gets the exact configured push URL.
    /// </summary>
    internal RemoteUrl Url { get; }

    /// <inheritdoc />
    public override string ToString()
        => $"{Index + 1}: {Url.RedactedDisplayText}";
}
