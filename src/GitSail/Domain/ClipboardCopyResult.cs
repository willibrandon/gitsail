namespace GitSail.Domain;

/// <summary>
/// Reports whether a clipboard request completed and whether its destination confirmed acceptance.
/// </summary>
/// <param name="Succeeded">Whether the selected mechanism completed without a reported error.</param>
/// <param name="Confirmed">Whether a platform helper confirmed successful completion.</param>
/// <param name="Message">The concise control-safe result shown to the user.</param>
internal sealed record ClipboardCopyResult(
    bool Succeeded,
    bool Confirmed,
    string Message);
