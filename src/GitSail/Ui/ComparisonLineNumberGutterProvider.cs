using Hex1b;
using Hex1b.Documents;
using Hex1b.Theming;
using System.Collections.Immutable;
using System.Globalization;

namespace GitSail.Ui;

/// <summary>
/// Renders repository file line numbers instead of comparison presentation row numbers.
/// </summary>
internal sealed class ComparisonLineNumberGutterProvider : IGutterProvider
{
    private readonly ImmutableArray<ComparisonLineNumber> _lineNumbers;
    private readonly bool _showOld;
    private readonly bool _showNew;
    private readonly int _digitCount;

    /// <summary>
    /// Initializes one old-side, new-side, or dual semantic line-number gutter.
    /// </summary>
    /// <param name="lineNumbers">The immutable presentation-row mappings.</param>
    /// <param name="showOld">Whether to render old-side file line numbers.</param>
    /// <param name="showNew">Whether to render new-side file line numbers.</param>
    internal ComparisonLineNumberGutterProvider(
        ImmutableArray<ComparisonLineNumber> lineNumbers,
        bool showOld,
        bool showNew)
    {
        if (!showOld && !showNew)
        {
            throw new ArgumentException("At least one comparison side must be shown.");
        }

        _lineNumbers = lineNumbers;
        _showOld = showOld;
        _showNew = showNew;
        var maximum = lineNumbers.IsEmpty
            ? 0
            : lineNumbers.Max(static line => Math.Max(line.OldLine ?? 0, line.NewLine ?? 0));
        _digitCount = Math.Max(
            2,
            maximum.ToString(CultureInfo.InvariantCulture).Length);
    }

    /// <summary>
    /// Returns the fixed width needed for the selected semantic line-number columns.
    /// </summary>
    /// <param name="document">The active comparison document.</param>
    /// <returns>The gutter width in terminal cells.</returns>
    public int GetWidth(IHex1bDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var columns = (_showOld ? 1 : 0) + (_showNew ? 1 : 0);
        return (_digitCount * columns) + columns;
    }

    /// <summary>
    /// Renders old and new file coordinates for one visible presentation row.
    /// </summary>
    /// <param name="context">The terminal render context.</param>
    /// <param name="theme">The active terminal theme.</param>
    /// <param name="screenX">The first gutter screen column.</param>
    /// <param name="screenY">The gutter screen row.</param>
    /// <param name="docLine">The one-based presentation row.</param>
    /// <param name="width">The allocated gutter width.</param>
    public void RenderLine(
        Hex1bRenderContext context,
        Hex1bTheme theme,
        int screenX,
        int screenY,
        int docLine,
        int width)
    {
        var lineNumber = docLine > 0 && docLine <= _lineNumbers.Length
            ? _lineNumbers[docLine - 1]
            : default;
        var text = BuildText(lineNumber);
        var foreground = theme.Get(GutterTheme.LineNumberForegroundColor);
        var background = theme.Get(GutterTheme.BackgroundColor);
        if (background.IsDefault)
        {
            background = theme.Get(EditorTheme.BackgroundColor);
        }

        context.WriteClipped(
            screenX,
            screenY,
            $"{foreground.ToForegroundAnsi()}{background.ToBackgroundAnsi()}{text.PadRight(width)}");
    }

    private string BuildText(ComparisonLineNumber lineNumber)
    {
        var separator = _showOld && _showNew ? " " : string.Empty;
        var oldText = _showOld ? Format(lineNumber.OldLine) : string.Empty;
        var newText = _showNew ? Format(lineNumber.NewLine) : string.Empty;
        return oldText + separator + newText + "│";
    }

    private string Format(int? lineNumber)
        => lineNumber?.ToString(CultureInfo.InvariantCulture).PadLeft(_digitCount) ??
            new string(' ', _digitCount);
}
