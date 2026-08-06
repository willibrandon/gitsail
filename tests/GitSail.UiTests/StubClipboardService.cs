using GitSail.Domain;

namespace GitSail.UiTests;

/// <summary>
/// Captures one classified UI clipboard request without emitting terminal control sequences.
/// </summary>
internal sealed class StubClipboardService : IClipboardService
{
    /// <summary>
    /// Gets or initializes whether the captured copy request succeeds.
    /// </summary>
    internal bool Succeeds { get; init; } = true;

    /// <summary>
    /// Gets the exact text supplied by the most recent copy request.
    /// </summary>
    internal string? Text { get; private set; }

    /// <summary>
    /// Gets the classification supplied by the most recent copy request.
    /// </summary>
    internal ClipboardContentClassification? Classification { get; private set; }

    /// <inheritdoc />
    public Task<ClipboardCopyResult> CopyAsync(
        string text,
        ClipboardContentClassification classification,
        Action<string> sendOsc52,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sendOsc52);
        cancellationToken.ThrowIfCancellationRequested();
        Text = text;
        Classification = classification;
        return Task.FromResult(new ClipboardCopyResult(
            Succeeded: Succeeds,
            Confirmed: Succeeds,
            Succeeds ? "Clipboard test confirmed." : "Clipboard test blocked."));
    }
}
