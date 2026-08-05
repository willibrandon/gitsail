namespace GitSail.Analyzers;

/// <summary>
/// Represents either literal message text or one named argument reference.
/// </summary>
internal readonly struct LocalizationPatternPart
{
    /// <summary>
    /// Initializes one parsed localization pattern part.
    /// </summary>
    /// <param name="text">The literal text or argument name.</param>
    /// <param name="isArgument">Whether <paramref name="text"/> names an argument.</param>
    internal LocalizationPatternPart(string text, bool isArgument)
    {
        Text = text;
        IsArgument = isArgument;
    }

    /// <summary>
    /// Gets the literal text or argument name.
    /// </summary>
    internal string Text { get; }

    /// <summary>
    /// Gets whether <see cref="Text"/> names an argument.
    /// </summary>
    internal bool IsArgument { get; }
}
