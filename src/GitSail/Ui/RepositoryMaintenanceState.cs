using GitSail.Domain;
using Hex1b.Documents;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Owns repository statistics and the latest read-only maintenance output presentation.
/// </summary>
internal sealed class RepositoryMaintenanceState
{
    /// <summary>
    /// Initializes an empty repository-care presentation.
    /// </summary>
    internal RepositoryMaintenanceState()
    {
        Output = CreateEditor("No repository maintenance or verification output yet.");
    }

    /// <summary>
    /// Gets the latest parsed object-database statistics, when loaded.
    /// </summary>
    internal RepositoryStatistics? Statistics { get; private set; }

    /// <summary>
    /// Gets the control-safe title for the latest operation output.
    /// </summary>
    internal string OutputTitle { get; private set; } = "Repository care output";

    /// <summary>
    /// Gets the latest combined read-only standard-output and standard-error presentation.
    /// </summary>
    internal EditorState Output { get; private set; }

    /// <summary>
    /// Applies a newly captured statistics snapshot.
    /// </summary>
    /// <param name="statistics">The complete parsed statistics.</param>
    internal void SetStatistics(RepositoryStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        Statistics = statistics;
    }

    /// <summary>
    /// Replaces the latest output with separately labeled terminal-safe channels.
    /// </summary>
    /// <param name="title">The control-safe operation title.</param>
    /// <param name="standardOutput">The exact bounded standard-output bytes.</param>
    /// <param name="standardError">The exact bounded standard-error bytes.</param>
    internal void SetOutput(
        string title,
        ReadOnlySpan<byte> standardOutput,
        ReadOnlySpan<byte> standardError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        OutputTitle = title;
        var output = TerminalOutputFormatter.Format(standardOutput);
        var error = TerminalOutputFormatter.Format(standardError);
        Output = CreateEditor(
            $"standard output:\n{(output.Length == 0 ? "<empty>" : output)}\n\n" +
            $"standard error:\n{(error.Length == 0 ? "<empty>" : error)}");
    }

    private static EditorState CreateEditor(string text)
        => new(new Hex1bDocument(text))
        {
            IsReadOnly = true,
        };
}
