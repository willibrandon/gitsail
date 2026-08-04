using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Contains the complete explicitly constructed environment for one child process.
/// </summary>
internal sealed class ChildEnvironment
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly ImmutableDictionary<string, string> _variables;
    private readonly ImmutableDictionary<string, byte[]> _unixVariables;

    private ChildEnvironment(
        ImmutableDictionary<string, string> variables,
        ImmutableDictionary<string, byte[]> unixVariables)
    {
        _variables = variables;
        _unixVariables = unixVariables;
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

        return new ChildEnvironment(
            builder.ToImmutable(),
            ImmutableDictionary.Create<string, byte[]>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Returns an environment with one exact native Unix value inserted or replaced.
    /// </summary>
    /// <param name="name">The environment variable name.</param>
    /// <param name="value">The non-NUL native value bytes.</param>
    /// <returns>A new immutable child environment.</returns>
    internal ChildEnvironment SetUnixValue(string name, ReadOnlySpan<byte> value)
    {
        ValidateName(name);
        if (value.Contains((byte)0))
        {
            throw new ArgumentException($"Environment variable '{name}' contains NUL.", nameof(value));
        }

        var variables = _variables.Remove(name);
        var unixVariables = _unixVariables.SetItem(name, value.ToArray());
        return new ChildEnvironment(variables, unixVariables);
    }

    /// <summary>
    /// Returns an environment with one exact text value inserted or replaced.
    /// </summary>
    /// <param name="name">The environment variable name.</param>
    /// <param name="value">The non-NUL text value.</param>
    /// <returns>A new immutable child environment.</returns>
    internal ChildEnvironment SetValue(string name, string value)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Environment variable '{name}' contains NUL.", nameof(value));
        }

        return new ChildEnvironment(
            _variables.SetItem(name, value),
            _unixVariables.Remove(name));
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

        if (!_unixVariables.IsEmpty)
        {
            throw new PlatformNotSupportedException(
                "A native Unix environment value cannot be represented by the Windows process boundary.");
        }

        foreach (var pair in _variables)
        {
            destination.Add(pair.Key, pair.Value);
        }
    }

    /// <summary>
    /// Gets one explicitly included child variable without exposing a mutable environment map.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The included value when present.</param>
    /// <returns><see langword="true"/> when the variable is included.</returns>
    internal bool TryGetValue(string name, out string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _variables.TryGetValue(name, out value);
    }

    /// <summary>
    /// Builds the complete exact native Unix environment block entries.
    /// </summary>
    /// <returns>Owned non-NUL byte arrays in <c>name=value</c> form.</returns>
    internal ImmutableArray<byte[]> GetUnixEntries()
    {
        var entries = ImmutableArray.CreateBuilder<byte[]>(
            checked(_variables.Count + _unixVariables.Count));
        foreach (var pair in _variables)
        {
            entries.Add(CreateUnixEntry(pair.Key, s_strictUtf8.GetBytes(pair.Value)));
        }

        foreach (var pair in _unixVariables)
        {
            entries.Add(CreateUnixEntry(pair.Key, pair.Value));
        }

        return entries.MoveToImmutable();
    }

    private static byte[] CreateUnixEntry(string name, ReadOnlySpan<byte> value)
    {
        var nameBytes = s_strictUtf8.GetBytes(name);
        var entry = new byte[checked(nameBytes.Length + 1 + value.Length)];
        nameBytes.CopyTo(entry, 0);
        entry[nameBytes.Length] = (byte)'=';
        value.CopyTo(entry.AsSpan(nameBytes.Length + 1));
        return entry;
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
