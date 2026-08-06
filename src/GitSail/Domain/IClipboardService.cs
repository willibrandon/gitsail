namespace GitSail.Domain;

/// <summary>
/// Applies clipboard policy before text reaches a platform helper or terminal request.
/// </summary>
internal interface IClipboardService
{
    /// <summary>
    /// Copies classified text through the currently configured mechanism.
    /// </summary>
    /// <param name="text">The exact text explicitly selected for copying.</param>
    /// <param name="classification">The content classification enforced before output.</param>
    /// <param name="sendOsc52">Emits an OSC 52 request through the active terminal application.</param>
    /// <param name="cancellationToken">Signals application shutdown or operation cancellation.</param>
    /// <returns>The honest confirmed, unconfirmed, disabled, or failed result.</returns>
    internal Task<ClipboardCopyResult> CopyAsync(
        string text,
        ClipboardContentClassification classification,
        Action<string> sendOsc52,
        CancellationToken cancellationToken);
}
