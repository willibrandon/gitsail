using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Indexes one validated exact unified hunk and its original line slices.
/// </summary>
/// <param name="Offset">The byte offset of the hunk header relative to the file patch.</param>
/// <param name="Length">The exact aggregate hunk byte length.</param>
/// <param name="HeaderLength">The exact hunk-header byte length including its terminator.</param>
/// <param name="StartLineNumber">The one-based presentation line containing the hunk header.</param>
/// <param name="EndLineNumber">The inclusive one-based final presentation line in the hunk.</param>
/// <param name="OldStart">The old-side starting line from the hunk header.</param>
/// <param name="OldCount">The old-side line count from the hunk header.</param>
/// <param name="NewStart">The new-side starting line from the hunk header.</param>
/// <param name="NewCount">The new-side line count from the hunk header.</param>
/// <param name="Lines">The exact ordered content-line index excluding the hunk header.</param>
internal sealed record RawPatchHunk(
    int Offset,
    int Length,
    int HeaderLength,
    int StartLineNumber,
    int EndLineNumber,
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    ImmutableArray<RawPatchLine> Lines);
