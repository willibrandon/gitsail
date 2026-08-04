using Hex1b.Documents;
using Hex1b.LanguageServer;
using Hex1b.Theming;
using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Combines baseline patch coloring with higher-contrast intraline comparison ranges.
/// </summary>
internal sealed class ComparisonDecorationProvider : ITextDecorationProvider
{
    private static readonly TextDecoration s_addition = new()
    {
        Background = Hex1bColor.FromRgb(35, 85, 35),
        Bold = true,
    };

    private static readonly TextDecoration s_deletion = new()
    {
        Background = Hex1bColor.FromRgb(85, 35, 35),
        Bold = true,
    };

    private readonly GitDiffDecorationProvider _baseProvider = new();
    private readonly ImmutableArray<ComparisonHighlight> _highlights;

    /// <summary>
    /// Initializes a provider over immutable presentation-relative highlight ranges.
    /// </summary>
    /// <param name="highlights">The exact intraline ranges for one editor document.</param>
    internal ComparisonDecorationProvider(ImmutableArray<ComparisonHighlight> highlights)
    {
        _highlights = highlights;
    }

    /// <summary>
    /// Connects baseline patch coloring to the active editor session.
    /// </summary>
    /// <param name="session">The active editor session.</param>
    public void Activate(IEditorSession session)
    {
        _baseProvider.Activate(session);
    }

    /// <summary>
    /// Returns baseline and intraline decorations intersecting the visible line range.
    /// </summary>
    /// <param name="startLine">The first visible one-based line.</param>
    /// <param name="endLine">The last visible one-based line.</param>
    /// <param name="document">The active comparison document.</param>
    /// <returns>The applicable baseline and higher-priority intraline spans.</returns>
    public IReadOnlyList<TextDecorationSpan> GetDecorations(
        int startLine,
        int endLine,
        IHex1bDocument document)
    {
        var baseline = _baseProvider.GetDecorations(startLine, endLine, document);
        var result = new List<TextDecorationSpan>(baseline.Count + _highlights.Length);
        result.AddRange(baseline);
        foreach (var highlight in _highlights)
        {
            if (highlight.Line < startLine || highlight.Line > endLine)
            {
                continue;
            }

            result.Add(new TextDecorationSpan(
                new DocumentPosition(highlight.Line, highlight.StartColumn),
                new DocumentPosition(highlight.Line, highlight.EndColumn),
                highlight.IsAddition ? s_addition : s_deletion,
                Priority: 100));
        }

        return result;
    }

    /// <summary>
    /// Disconnects baseline patch coloring from the editor session.
    /// </summary>
    public void Deactivate()
    {
        _baseProvider.Deactivate();
    }
}
