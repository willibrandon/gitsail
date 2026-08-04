namespace GitSail.Git.Execution;

/// <summary>
/// Represents one literal child-process argument that is never interpreted by a shell.
/// </summary>
internal sealed record ProcessArgument
{
    private ProcessArgument(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Gets the literal argument value.
    /// </summary>
    internal string Value { get; }

    /// <summary>
    /// Creates one literal managed argument.
    /// </summary>
    /// <param name="value">The argument value.</param>
    /// <returns>The typed literal argument.</returns>
    internal static ProcessArgument Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A process argument cannot contain NUL.", nameof(value));
        }

        return new ProcessArgument(value);
    }

    /// <inheritdoc />
    public override string ToString()
        => Value;
}
