using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail.CommandLine;

/// <summary>
/// Contains the typed operands selected for the line-history workflow.
/// </summary>
/// <param name="Revision">The optional revision supplied by the user.</param>
/// <param name="Paths">The managed command-line file operands.</param>
/// <param name="Line">The optional one-based line to focus after loading.</param>
/// <param name="Range">The optional bounded line range requested from Git.</param>
/// <param name="DetectMoves">Whether Git should detect moved lines within the file.</param>
/// <param name="DetectCopies">Whether Git should detect lines copied from other files.</param>
/// <param name="PathspecFile">The optional pathspec input file or <c>-</c> for standard input.</param>
/// <param name="PathspecFileNul">Whether the pathspec input must contain NUL-delimited records.</param>
/// <param name="NativePaths">The exact native operands following <c>--</c>, when present.</param>
internal sealed record BlameOptions(
    string? Revision,
    ImmutableArray<string> Paths,
    int? Line = null,
    string? Range = null,
    bool DetectMoves = false,
    bool DetectCopies = false,
    string? PathspecFile = null,
    bool PathspecFileNul = false,
    ImmutableArray<GitPath>? NativePaths = null);
