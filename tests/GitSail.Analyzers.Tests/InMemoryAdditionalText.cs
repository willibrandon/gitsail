using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace GitSail.Analyzers.Tests;

/// <summary>
/// Supplies one in-memory additional file to a source-generator test.
/// </summary>
internal sealed class InMemoryAdditionalText : AdditionalText
{
    private readonly SourceText _text;

    /// <summary>
    /// Initializes one in-memory additional file.
    /// </summary>
    /// <param name="path">The synthetic source path.</param>
    /// <param name="contents">The complete file contents.</param>
    internal InMemoryAdditionalText(string path, string contents)
    {
        Path = path;
        _text = SourceText.From(contents, Encoding.UTF8);
    }

    /// <inheritdoc />
    public override string Path { get; }

    /// <inheritdoc />
    public override SourceText GetText(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _text;
    }
}
