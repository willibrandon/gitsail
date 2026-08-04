namespace GitSail.Git.Execution;

/// <summary>
/// Represents child output that exceeded its declared bounded-capture policy.
/// </summary>
internal sealed class ProcessOutputLimitExceededException : IOException
{
    /// <summary>
    /// Initializes a bounded-output failure for the named stream.
    /// </summary>
    /// <param name="streamName">The output stream whose limit was exceeded.</param>
    /// <param name="maximumBytes">The configured maximum byte count.</param>
    internal ProcessOutputLimitExceededException(string streamName, int maximumBytes)
        : base($"Child {streamName} exceeded its {maximumBytes}-byte capture limit.")
    {
    }
}
