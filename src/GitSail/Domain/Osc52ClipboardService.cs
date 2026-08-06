namespace GitSail.Domain;

/// <summary>
/// Supplies the conservative terminal-only clipboard behavior used by isolated view tests.
/// </summary>
internal sealed class Osc52ClipboardService : IClipboardService
{
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
        if (classification == ClipboardContentClassification.Secret)
        {
            return Task.FromResult(new ClipboardCopyResult(
                Succeeded: false,
                Confirmed: false,
                "Secret values cannot be copied to the clipboard."));
        }

        sendOsc52(text);
        return Task.FromResult(new ClipboardCopyResult(
            Succeeded: true,
            Confirmed: false,
            "Sent an OSC 52 clipboard request; the terminal did not confirm whether it was accepted."));
    }
}
