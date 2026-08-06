namespace GitSail.Ui;

/// <summary>
/// Defines the output-only Unicode policy applied at the terminal boundary.
/// Keeps repository and application text unchanged before final presentation.
/// </summary>
/// <param name="UseAscii">Whether visible non-ASCII graphemes require width-preserving ASCII replacements.</param>
/// <param name="AmbiguousWidth">The terminal width assigned to East Asian Width ambiguous characters.</param>
internal readonly record struct TerminalTextPolicy(
    bool UseAscii,
    int AmbiguousWidth)
{
    /// <summary>
    /// Gets whether terminal output requires a presentation-boundary transformation.
    /// Avoids decoding ordinary Unicode output when the baseline policy already matches.
    /// </summary>
    internal bool RequiresTransformation => UseAscii || AmbiguousWidth == 2;
}
