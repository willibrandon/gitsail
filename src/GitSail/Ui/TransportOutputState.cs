using Hex1b.Documents;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Owns separate lifted read-only standard-output and standard-error transport presentations.
/// </summary>
internal sealed class TransportOutputState
{
    /// <summary>
    /// Initializes an empty transport-console presentation.
    /// </summary>
    internal TransportOutputState()
    {
        StandardOutput = CreateEditor("No remote operation output yet.");
        StandardError = CreateEditor("No remote operation diagnostics yet.");
    }

    /// <summary>
    /// Gets the control-safe title for the most recent transport operation.
    /// </summary>
    internal string Title { get; private set; } = "Remote output";

    /// <summary>
    /// Gets the read-only standard-output editor presentation.
    /// </summary>
    internal EditorState StandardOutput { get; private set; }

    /// <summary>
    /// Gets the read-only standard-error editor presentation.
    /// </summary>
    internal EditorState StandardError { get; private set; }

    /// <summary>
    /// Replaces both transport channels after a completed remote operation.
    /// </summary>
    /// <param name="title">The control-safe completed-operation title.</param>
    /// <param name="standardOutput">The decoded, redacted, and terminal-safe standard output.</param>
    /// <param name="standardError">The decoded, redacted, and terminal-safe standard error.</param>
    internal void Set(string title, string standardOutput, string standardError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        Title = title;
        StandardOutput = CreateEditor(string.IsNullOrEmpty(standardOutput) ? "<empty>" : standardOutput);
        StandardError = CreateEditor(string.IsNullOrEmpty(standardError) ? "<empty>" : standardError);
    }

    private static EditorState CreateEditor(string text)
        => new(new Hex1bDocument(text))
        {
            IsReadOnly = true,
        };
}
