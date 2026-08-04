using System.Collections.Immutable;

namespace GitSail.CommandLine;

/// <summary>
/// Contains the typed operands selected for the repository tree browser.
/// </summary>
/// <param name="Revision">The optional revision supplied by the user.</param>
/// <param name="Directories">The managed command-line directory operands.</param>
/// <param name="PathspecFile">The optional pathspec input file or <c>-</c> for standard input.</param>
/// <param name="PathspecFileNul">Whether the pathspec input must contain NUL-delimited records.</param>
internal sealed record BrowserOptions(
    string? Revision,
    ImmutableArray<string> Directories,
    string? PathspecFile = null,
    bool PathspecFileNul = false);
