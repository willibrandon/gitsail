using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Presents one attributed file line while retaining its exact structured origin metadata.
/// </summary>
internal sealed class BlameWorkspaceItem
{
    /// <summary>
    /// Initializes one terminal-safe line presentation over exact attribution metadata.
    /// </summary>
    /// <param name="attribution">The exact Git attribution for the result line.</param>
    /// <param name="content">The decoded terminal-safe content line.</param>
    internal BlameWorkspaceItem(BlameAttribution attribution, string content)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        ArgumentNullException.ThrowIfNull(content);
        Attribution = attribution;
        Content = content;
    }

    /// <summary>
    /// Gets the exact origin metadata associated with the displayed line.
    /// </summary>
    internal BlameAttribution Attribution { get; }

    /// <summary>
    /// Gets the terminal-safe decoded line content used only for presentation.
    /// </summary>
    internal string Content { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        var commit = Attribution.Commit;
        var identity = commit.IsUncommitted ? "worktree" : commit.ObjectId.ToString()[..8];
        var author = Decode(commit.AuthorName.Span, "unknown");
        var displayAuthor = author.Length > 12 ? author[..12] : author;
        return $"{Attribution.ResultLineNumber,5} {identity} {displayAuthor,-12}  {Content}";
    }

    private static string Decode(ReadOnlySpan<byte> bytes, string emptyValue)
        => bytes.IsEmpty ? emptyValue : GitPath.FromUnixBytes(bytes).DisplayText;
}
