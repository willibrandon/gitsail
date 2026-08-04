namespace GitSail.Diagnostics;

/// <summary>
/// Contains one sanitized trace event for the in-application log drawer.
/// </summary>
/// <param name="Timestamp">The UTC event time.</param>
/// <param name="EventName">The stable event name.</param>
/// <param name="Message">The control-safe human-readable summary.</param>
internal sealed record TraceDisplayEntry(
    DateTimeOffset Timestamp,
    string EventName,
    string Message)
{
    /// <inheritdoc />
    public override string ToString()
        => $"{Timestamp:HH:mm:ss.fff}  {EventName}  {Message}";
}
