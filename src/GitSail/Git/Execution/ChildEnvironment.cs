using System.Collections.Immutable;

namespace GitSail.Git.Execution;

/// <summary>
/// Contains the complete explicitly constructed environment for one child process.
/// </summary>
internal sealed class ChildEnvironment
{
    private readonly ImmutableDictionary<string, string> _variables;

    private ChildEnvironment(ImmutableDictionary<string, string> variables)
    {
        _variables = variables;
    }

    /// <summary>
    /// Creates an immutable child environment from explicitly selected variables.
    /// </summary>
    /// <param name="variables">The complete variable set for the child.</param>
    /// <returns>The validated immutable child environment.</returns>
    internal static ChildEnvironment Create(IEnumerable<KeyValuePair<string, string>> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var builder = ImmutableDictionary.CreateBuilder<string, string>(GetNameComparer());
        foreach (var pair in variables)
        {
            ValidateName(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            if (pair.Value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Environment variable '{pair.Key}' contains NUL.", nameof(variables));
            }

            if (!builder.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException($"Environment variable '{pair.Key}' is duplicated.", nameof(variables));
            }
        }

        return new ChildEnvironment(builder.ToImmutable());
    }

    /// <summary>
    /// Copies the complete child environment into a process start configuration.
    /// </summary>
    /// <param name="destination">The initially empty destination environment.</param>
    internal void CopyTo(IDictionary<string, string?> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Count != 0)
        {
            throw new ArgumentException("The destination child environment must be empty.", nameof(destination));
        }

        foreach (var pair in _variables)
        {
            destination.Add(pair.Key, pair.Value);
        }
    }

    private static StringComparer GetNameComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('=', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("An environment variable name cannot contain '=' or NUL.", nameof(name));
        }
    }
}
