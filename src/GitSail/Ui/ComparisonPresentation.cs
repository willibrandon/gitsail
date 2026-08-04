using System.Collections.Immutable;

namespace GitSail.Ui;

/// <summary>
/// Contains unified and aligned two-pane text derived from one immutable raw file patch.
/// </summary>
/// <param name="UnifiedText">The control-safe unified patch presentation.</param>
/// <param name="LeftText">The aligned control-safe left-side presentation.</param>
/// <param name="RightText">The aligned control-safe right-side presentation.</param>
/// <param name="UnifiedHunkLines">The one-based unified editor lines containing hunk headers.</param>
/// <param name="SideHunkLines">The one-based aligned editor lines containing hunk headers.</param>
internal sealed record ComparisonPresentation(
    string UnifiedText,
    string LeftText,
    string RightText,
    ImmutableArray<int> UnifiedHunkLines,
    ImmutableArray<int> SideHunkLines);
