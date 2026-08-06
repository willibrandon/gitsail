namespace GitSail.Domain;

/// <summary>
/// Selects the configured mechanism used for explicit clipboard copy requests.
/// </summary>
internal enum ClipboardPolicy
{
    /// <summary>
    /// Disables every clipboard copy mechanism.
    /// </summary>
    Off,

    /// <summary>
    /// Prefers a confirmable platform helper and falls back to an unconfirmed terminal request.
    /// </summary>
    Auto,

    /// <summary>
    /// Sends only an unconfirmed OSC 52 terminal request.
    /// </summary>
    Osc52,

    /// <summary>
    /// Uses only a supported platform clipboard helper.
    /// </summary>
    Helper,
}
