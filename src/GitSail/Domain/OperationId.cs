namespace GitSail.Domain;

/// <summary>
/// Identifies one operation owned by an operation supervisor.
/// </summary>
internal readonly record struct OperationId : IComparable<OperationId>
{
    /// <summary>
    /// Initializes a positive operation identifier.
    /// </summary>
    /// <param name="value">The positive identifier value.</param>
    internal OperationId(long value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        Value = value;
    }

    /// <summary>
    /// Gets the numeric identifier value.
    /// </summary>
    internal long Value { get; }

    /// <inheritdoc />
    public int CompareTo(OperationId other)
        => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString()
        => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
