namespace GitSail.Domain;

/// <summary>
/// Identifies one monotonically increasing repository operation generation.
/// </summary>
internal readonly record struct OperationGeneration : IComparable<OperationGeneration>
{
    /// <summary>
    /// Initializes a nonnegative operation generation.
    /// </summary>
    /// <param name="value">The nonnegative generation value.</param>
    internal OperationGeneration(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    /// <summary>
    /// Gets the numeric generation value.
    /// </summary>
    internal long Value { get; }

    /// <summary>
    /// Creates the immediately following generation.
    /// </summary>
    /// <returns>The next generation.</returns>
    internal OperationGeneration Next()
        => new(checked(Value + 1));

    /// <inheritdoc />
    public int CompareTo(OperationGeneration other)
        => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString()
        => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
