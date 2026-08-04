using System.Text;

namespace GitSail.Domain;

/// <summary>
/// Retains exact Git remote URL bytes and provides a credential-redacted display value.
/// </summary>
internal sealed class RemoteUrl : IEquatable<RemoteUrl>
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly byte[] _bytes;

    private RemoteUrl(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>
    /// Gets a control-safe display value with URL user information and query values removed.
    /// </summary>
    internal string RedactedDisplayText => FormatRedacted(_bytes);

    /// <summary>
    /// Creates a remote URL from exact non-NUL configuration bytes.
    /// </summary>
    /// <param name="bytes">The URL bytes, which may be explicitly empty in malformed configuration.</param>
    /// <returns>A remote URL that owns a copy of the supplied bytes.</returns>
    internal static RemoteUrl FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains((byte)0))
        {
            throw new ArgumentException("A Git remote URL cannot contain NUL.", nameof(bytes));
        }

        return new RemoteUrl(bytes.ToArray());
    }

    /// <summary>
    /// Creates a remote URL from user-entered Unicode text using strict UTF-8.
    /// </summary>
    /// <param name="value">The nonempty URL text supplied by the user.</param>
    /// <returns>The exact validated remote URL bytes.</returns>
    internal static RemoteUrl FromText(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A Git remote URL cannot contain NUL.", nameof(value));
        }

        try
        {
            return FromBytes(s_strictUtf8.GetBytes(value));
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("A Git remote URL contains invalid Unicode text.", nameof(value), exception);
        }
    }

    /// <summary>
    /// Gets the exact bytes retained by this remote URL.
    /// </summary>
    /// <returns>A read-only span over URL-owned bytes.</returns>
    internal ReadOnlySpan<byte> GetBytes()
        => _bytes;

    /// <summary>
    /// Replaces every exact decoded occurrence of this URL with its credential-redacted display value.
    /// </summary>
    /// <param name="text">The diagnostic or transport text that may contain this exact URL.</param>
    /// <returns>The text with this URL redacted before it can enter an exception or render sink.</returns>
    internal string RedactFrom(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var raw = Encoding.UTF8.GetString(_bytes);
        return string.IsNullOrEmpty(raw)
            ? text
            : text.Replace(raw, RedactedDisplayText, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool Equals(RemoteUrl? other)
        => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is RemoteUrl other && Equals(other);

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
        => RedactedDisplayText;

    private static string FormatRedacted(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return "<empty>";
        }

        var redacted = bytes.ToArray();
        var scheme = bytes.IndexOf("://"u8);
        if (scheme >= 1)
        {
            var authorityStart = scheme + 3;
            var authorityEnd = IndexOfAny(bytes[authorityStart..], (byte)'/', (byte)'?', (byte)'#');
            authorityEnd = authorityEnd < 0 ? bytes.Length : authorityStart + authorityEnd;
            var userInfo = bytes[authorityStart..authorityEnd].LastIndexOf((byte)'@');
            if (userInfo >= 0)
            {
                var removeLength = userInfo + 1;
                redacted = [.. bytes[..authorityStart], .. bytes[(authorityStart + removeLength)..]];
            }
        }
        else
        {
            var at = bytes.IndexOf((byte)'@');
            var colon = at < 0 ? -1 : bytes[(at + 1)..].IndexOf((byte)':');
            var slash = at < 0 ? -1 : bytes[..at].IndexOfAny((byte)'/', (byte)'\\');
            if (at > 0 && colon >= 0 && slash < 0)
            {
                redacted = bytes[(at + 1)..].ToArray();
            }
        }

        var query = redacted.AsSpan().IndexOf((byte)'?');
        if (query >= 0)
        {
            var fragment = redacted.AsSpan(query).IndexOf((byte)'#');
            var suffix = fragment < 0 ? ReadOnlySpan<byte>.Empty : redacted.AsSpan(query + fragment);
            redacted = [.. redacted.AsSpan(0, query), .. "?<redacted>"u8, .. suffix];
        }

        return GitPath.FromUnixBytes(redacted).DisplayText;
    }

    private static int IndexOfAny(ReadOnlySpan<byte> bytes, byte first, byte second, byte third)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == first || bytes[index] == second || bytes[index] == third)
            {
                return index;
            }
        }

        return -1;
    }
}
