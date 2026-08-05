using GitSail.Domain;
using GitSail.Localization.Generated;

namespace GitSail.Ui;

/// <summary>
/// Presents one structured commit with its bounded lane graph in the history list.
/// </summary>
internal sealed class HistoryWorkspaceItem
{
    /// <summary>
    /// Initializes one history row over an exact commit.
    /// </summary>
    /// <param name="commit">The exact structured commit.</param>
    /// <param name="graph">The bounded graph prefix for the commit's lane.</param>
    internal HistoryWorkspaceItem(HistoryCommit commit, string graph)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(graph);
        Commit = commit;
        Graph = graph;
    }

    /// <summary>
    /// Gets the exact structured commit backing this row.
    /// </summary>
    internal HistoryCommit Commit { get; }

    /// <summary>
    /// Gets the bounded text graph showing this commit's active lane.
    /// </summary>
    internal string Graph { get; }

    /// <summary>
    /// Returns one compact control-safe history row.
    /// </summary>
    /// <returns>The graph, abbreviated object identifier, and subject.</returns>
    public override string ToString()
    {
        var subject = Decode(Commit.Subject.Span, AppMessages.HistoryValueNoSubject);
        return $"{Graph} {Commit.ObjectId.ToString()[..12]}  {subject}";
    }

    private static string Decode(ReadOnlySpan<byte> bytes, string emptyValue)
        => bytes.IsEmpty
            ? emptyValue
            : TerminalTextSanitizer.Sanitize(GitPath.FromUnixBytes(bytes).DisplayText);
}
