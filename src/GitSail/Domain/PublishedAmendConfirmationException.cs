namespace GitSail.Domain;

/// <summary>
/// Reports that a locally published amend requires explicit user confirmation.
/// </summary>
internal sealed class PublishedAmendConfirmationException : InvalidOperationException
{
    /// <summary>
    /// Initializes a confirmation failure with the complete local publication warning.
    /// </summary>
    /// <param name="warning">The local remote-tracking refs that contain the commit.</param>
    internal PublishedAmendConfirmationException(PublishedAmendWarning warning)
        : base(CreateMessage(warning))
    {
        Warning = warning;
    }

    /// <summary>
    /// Gets the complete local publication warning that requires confirmation.
    /// </summary>
    internal PublishedAmendWarning Warning { get; }

    private static string CreateMessage(PublishedAmendWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        var refs = string.Join(", ", warning.RemoteTrackingRefs.Select(static reference => reference.DisplayText));
        return $"Amending HEAD requires confirmation because these local remote-tracking refs contain it: {refs}. " +
            "This is a local heuristic; the remote servers may differ from the local refs.";
    }
}
