namespace GitSail.Domain;

/// <summary>
/// Represents one untrusted revision expression that can only be used through typed validation.
/// </summary>
internal sealed record Revision
{
    private Revision(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Gets the untrusted revision expression.
    /// </summary>
    internal string Value { get; }

    /// <summary>
    /// Creates a nonempty revision expression without interpreting it as command syntax.
    /// </summary>
    /// <param name="value">The untrusted revision text.</param>
    /// <returns>The typed revision candidate.</returns>
    internal static Revision Create(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A Git revision cannot contain NUL.", nameof(value));
        }

        return new Revision(value);
    }

    /// <inheritdoc />
    public override string ToString()
        => Value;
}
