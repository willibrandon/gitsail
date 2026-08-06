using Hex1b.Documents;

namespace GitSail.Ui;

/// <summary>
/// Produces theme-aware decorations for complete unified Git patches.
/// </summary>
internal sealed class GitSailDiffDecorationProvider : ITextDecorationProvider
{
    private static readonly TextDecoration s_header = new()
    {
        ForegroundThemeElement = GitSailDiffTheme.HeaderForegroundColor,
        BackgroundThemeElement = GitSailDiffTheme.HeaderBackgroundColor,
        Bold = true,
    };
    private static readonly TextDecoration s_metadata = new()
    {
        ForegroundThemeElement = GitSailDiffTheme.MetadataForegroundColor,
        BackgroundThemeElement = GitSailDiffTheme.MetadataBackgroundColor,
    };
    private static readonly TextDecoration s_oldFile = new()
    {
        ForegroundThemeElement = GitSailDiffTheme.OldFileForegroundColor,
        BackgroundThemeElement = GitSailDiffTheme.OldFileBackgroundColor,
        Bold = true,
    };
    private static readonly TextDecoration s_newFile = new()
    {
        ForegroundThemeElement = GitSailDiffTheme.NewFileForegroundColor,
        BackgroundThemeElement = GitSailDiffTheme.NewFileBackgroundColor,
        Bold = true,
    };
    private static readonly TextDecoration s_hunk = new()
    {
        ForegroundThemeElement = GitSailDiffTheme.HunkForegroundColor,
        BackgroundThemeElement = GitSailDiffTheme.HunkBackgroundColor,
        Italic = true,
    };
    private static readonly TextDecoration s_addition = new()
    {
        ForegroundThemeElement = GitSailDiffTheme.AdditionForegroundColor,
        BackgroundThemeElement = GitSailDiffTheme.AdditionBackgroundColor,
    };
    private static readonly TextDecoration s_deletion = new()
    {
        ForegroundThemeElement = GitSailDiffTheme.DeletionForegroundColor,
        BackgroundThemeElement = GitSailDiffTheme.DeletionBackgroundColor,
    };
    private static readonly TextDecoration s_noNewline = new()
    {
        ForegroundThemeElement = GitSailDiffTheme.MetadataForegroundColor,
        Italic = true,
    };
    private long _documentVersion = -1;
    private IReadOnlyList<TextDecorationSpan> _spans = [];

    /// <summary>
    /// Connects this decoration provider to an editor session.
    /// </summary>
    /// <param name="session">The editor session displaying the patch.</param>
    public void Activate(IEditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
    }

    /// <summary>
    /// Gets every patch decoration intersecting a visible line range.
    /// </summary>
    /// <param name="startLine">The first visible one-based line.</param>
    /// <param name="endLine">The last visible one-based line.</param>
    /// <param name="document">The complete active patch document.</param>
    /// <returns>The cached theme-aware spans for the requested lines.</returns>
    public IReadOnlyList<TextDecorationSpan> GetDecorations(
        int startLine,
        int endLine,
        IHex1bDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != _documentVersion)
        {
            _documentVersion = document.Version;
            _spans = BuildSpans(document);
        }

        return
        [
            .. _spans.Where(span => span.Start.Line >= startLine && span.Start.Line <= endLine),
        ];
    }

    /// <summary>
    /// Releases cached decorations when the editor no longer uses this provider.
    /// </summary>
    public void Deactivate()
    {
        _documentVersion = -1;
        _spans = [];
    }

    private static List<TextDecorationSpan> BuildSpans(IHex1bDocument document)
    {
        var spans = new List<TextDecorationSpan>();
        var lineNumber = 0;
        foreach (var rawLine in document.GetText().Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r');
            var decoration = Classify(line);
            if (decoration is null)
            {
                continue;
            }

            spans.Add(new TextDecorationSpan(
                new DocumentPosition(lineNumber, 1),
                new DocumentPosition(lineNumber, line.Length + 1),
                decoration));
        }

        return spans;
    }

    private static TextDecoration? Classify(string line)
    {
        if (line.StartsWith("diff ", StringComparison.Ordinal))
        {
            return s_header;
        }

        if (line.StartsWith("index ", StringComparison.Ordinal) ||
            line.StartsWith("similarity index", StringComparison.Ordinal) ||
            line.StartsWith("rename from", StringComparison.Ordinal) ||
            line.StartsWith("rename to", StringComparison.Ordinal) ||
            line.StartsWith("old mode", StringComparison.Ordinal) ||
            line.StartsWith("new mode", StringComparison.Ordinal) ||
            line.StartsWith("new file mode", StringComparison.Ordinal) ||
            line.StartsWith("deleted file mode", StringComparison.Ordinal))
        {
            return s_metadata;
        }

        if (line.StartsWith("--- ", StringComparison.Ordinal))
        {
            return s_oldFile;
        }

        if (line.StartsWith("+++ ", StringComparison.Ordinal))
        {
            return s_newFile;
        }

        if (line.StartsWith("@@ ", StringComparison.Ordinal))
        {
            return s_hunk;
        }

        if (line.StartsWith('+'))
        {
            return s_addition;
        }

        if (line.StartsWith('-'))
        {
            return s_deletion;
        }

        return line.StartsWith("\\ No newline", StringComparison.Ordinal)
            ? s_noNewline
            : null;
    }
}
