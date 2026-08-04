namespace GitSail.Git.Execution;

/// <summary>
/// Defines independent byte limits for captured child output streams.
/// </summary>
/// <param name="MaximumStandardOutputBytes">The maximum retained standard-output byte count.</param>
/// <param name="MaximumStandardErrorBytes">The maximum retained standard-error byte count.</param>
/// <param name="StandardOutputSpoolMemoryThresholdBytes">The output spool memory threshold, or zero for memory-only capture.</param>
internal readonly record struct OutputPolicy(
    int MaximumStandardOutputBytes,
    int MaximumStandardErrorBytes,
    int StandardOutputSpoolMemoryThresholdBytes)
{
    /// <summary>
    /// Creates and validates an output-capture policy.
    /// </summary>
    /// <param name="maximumStandardOutputBytes">The positive standard-output limit.</param>
    /// <param name="maximumStandardErrorBytes">The positive standard-error limit.</param>
    /// <returns>The validated output policy.</returns>
    internal static OutputPolicy Create(int maximumStandardOutputBytes, int maximumStandardErrorBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStandardOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStandardErrorBytes);
        return new OutputPolicy(
            maximumStandardOutputBytes,
            maximumStandardErrorBytes,
            StandardOutputSpoolMemoryThresholdBytes: 0);
    }

    /// <summary>
    /// Creates a validated output policy that spills standard output after a memory threshold.
    /// </summary>
    /// <param name="memoryThresholdBytes">The positive standard-output in-memory threshold.</param>
    /// <param name="maximumStandardOutputBytes">The positive aggregate standard-output limit.</param>
    /// <param name="maximumStandardErrorBytes">The positive standard-error memory limit.</param>
    /// <returns>The validated spooling policy.</returns>
    internal static OutputPolicy CreateSpooling(
        int memoryThresholdBytes,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryThresholdBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStandardOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStandardErrorBytes);
        if (memoryThresholdBytes > maximumStandardOutputBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(memoryThresholdBytes),
                "The spool threshold cannot exceed the aggregate output limit.");
        }

        return new OutputPolicy(
            maximumStandardOutputBytes,
            maximumStandardErrorBytes,
            memoryThresholdBytes);
    }
}
