namespace GitSail.Ui;

/// <summary>
/// Maps one presentation row to its optional old-side and new-side file line numbers.
/// </summary>
/// <param name="OldLine">The one-based old-side file line, or no value for a non-content row.</param>
/// <param name="NewLine">The one-based new-side file line, or no value for a non-content row.</param>
internal readonly record struct ComparisonLineNumber(int? OldLine, int? NewLine);
