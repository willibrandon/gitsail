using Hex1b;
using Hex1b.Documents;

namespace GitSail.Ui;

/// <summary>
/// Produces theme-aware editor underlines for current commit-message spelling issues.
/// </summary>
internal sealed class SpellingDecorationProvider : ITextDecorationProvider
{
    private static readonly TextDecoration s_decoration = new()
    {
        UnderlineStyle = UnderlineStyle.Curly,
        UnderlineColorThemeElement = GitSailSpellingTheme.UnderlineColor,
    };
    private readonly SpellingState _state;

    /// <summary>
    /// Initializes a provider over one controlled spelling state.
    /// </summary>
    /// <param name="state">The version-matched spelling result source.</param>
    internal SpellingDecorationProvider(SpellingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <summary>
    /// Connects the provider to the active editor session.
    /// </summary>
    /// <param name="session">The editor session displaying the commit message.</param>
    public void Activate(IEditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
    }

    /// <summary>
    /// Gets every current spelling underline intersecting the visible line range.
    /// </summary>
    /// <param name="startLine">The first visible one-based line.</param>
    /// <param name="endLine">The last visible one-based line.</param>
    /// <param name="document">The complete active commit-message document.</param>
    /// <returns>The current bounded spelling decoration spans.</returns>
    public IReadOnlyList<TextDecorationSpan> GetDecorations(
        int startLine,
        int endLine,
        IHex1bDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var issues = _state.GetIssues(document.Version);
        if (issues.IsEmpty)
        {
            return [];
        }

        var spans = new List<TextDecorationSpan>(issues.Length);
        foreach (var issue in issues)
        {
            var start = document.OffsetToPosition(new DocumentOffset(issue.Offset));
            var end = document.OffsetToPosition(new DocumentOffset(issue.Offset + issue.Length));
            if (end.Line < startLine || start.Line > endLine)
            {
                continue;
            }

            spans.Add(new TextDecorationSpan(start, end, s_decoration, Priority: 200));
        }

        return spans;
    }

    /// <summary>
    /// Releases the stateless connection when the editor stops using this provider.
    /// </summary>
    public void Deactivate()
    {
    }
}
