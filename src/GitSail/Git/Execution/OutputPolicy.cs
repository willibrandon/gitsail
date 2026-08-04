namespace GitSail.Git.Execution;

/// <summary>
/// Defines independent byte limits for captured child output streams.
/// </summary>
/// <param name="MaximumStandardOutputBytes">The maximum retained standard-output byte count.</param>
/// <param name="MaximumStandardErrorBytes">The maximum retained standard-error byte count.</param>
internal readonly record struct OutputPolicy(
    int MaximumStandardOutputBytes,
    int MaximumStandardErrorBytes)
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
        return new OutputPolicy(maximumStandardOutputBytes, maximumStandardErrorBytes);
    }
}
