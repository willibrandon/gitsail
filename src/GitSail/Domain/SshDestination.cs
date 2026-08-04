using System.Text;

namespace GitSail.Domain;

/// <summary>
/// Retains one validated SSH user and host independently from shell command text.
/// </summary>
internal sealed class SshDestination : IEquatable<SshDestination>
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly byte[] _bytes;

    /// <summary>
    /// Initializes one validated destination argument from user, host, and IPv6 shape.
    /// </summary>
    /// <param name="user">The optional SSH user.</param>
    /// <param name="host">The required SSH host without brackets.</param>
    /// <param name="isIpv6">Whether the host requires URI-style brackets in the destination argument.</param>
    internal SshDestination(string? user, string host, bool isIpv6)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ValidateComponent(host, "host");
        if (user is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(user);
            ValidateComponent(user, "user");
        }

        User = user;
        Host = host;
        IsIpv6 = isIpv6;
        var text = $"{(user is null ? string.Empty : $"{user}@")}{(isIpv6 ? $"[{host}]" : host)}";
        _bytes = s_strictUtf8.GetBytes(text);
    }

    /// <summary>
    /// Gets the optional exact SSH user text.
    /// </summary>
    internal string? User { get; }

    /// <summary>
    /// Gets the exact SSH host text without IPv6 brackets.
    /// </summary>
    internal string Host { get; }

    /// <summary>
    /// Gets whether the destination argument brackets an IPv6 literal.
    /// </summary>
    internal bool IsIpv6 { get; }

    /// <summary>
    /// Gets the complete validated destination argument bytes.
    /// </summary>
    /// <returns>A read-only span over destination-owned UTF-8 bytes.</returns>
    internal ReadOnlySpan<byte> GetBytes()
        => _bytes;

    /// <inheritdoc />
    public bool Equals(SshDestination? other)
        => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is SshDestination other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in _bytes)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
        => GitPath.FromUnixBytes(_bytes).DisplayText;

    private static void ValidateComponent(string value, string name)
    {
        if (value[0] == '-' || value.Any(static character =>
            char.IsControl(character) || char.IsWhiteSpace(character) || character is '@' or '\0'))
        {
            throw new ArgumentException($"An SSH {name} contains an unsafe character.", name);
        }
    }
}
