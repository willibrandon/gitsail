using System.Buffers;
using System.Globalization;
using System.Text;

namespace GitSail.Domain;

/// <summary>
/// Retains a Git path in its exact native representation independently from display text.
/// </summary>
internal sealed class GitPath : IEquatable<GitPath>, IComparable<GitPath>
{
    private readonly byte[]? _unixBytes;
    private readonly string? _windowsPath;

    private GitPath(byte[] unixBytes)
    {
        _unixBytes = unixBytes;
        Kind = NativePathKind.UnixBytes;
    }

    private GitPath(string windowsPath)
    {
        _windowsPath = windowsPath;
        Kind = NativePathKind.WindowsUtf16;
    }

    /// <summary>
    /// Gets the native representation retained by this path.
    /// </summary>
    internal NativePathKind Kind { get; }

    /// <summary>
    /// Gets a control-sanitized representation intended only for display.
    /// </summary>
    internal string DisplayText => Kind == NativePathKind.UnixBytes
        ? FormatUnixBytes(_unixBytes!)
        : FormatWindowsText(_windowsPath!);

    /// <summary>
    /// Creates a path from exact Unix filename bytes.
    /// </summary>
    /// <param name="bytes">The nonempty native byte sequence.</param>
    /// <returns>A path that owns a copy of the supplied bytes.</returns>
    internal static GitPath FromUnixBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new ArgumentException("A Git path cannot be empty.", nameof(bytes));
        }

        if (bytes.Contains((byte)0))
        {
            throw new ArgumentException("A Git path cannot contain NUL.", nameof(bytes));
        }

        return new GitPath(bytes.ToArray());
    }

    /// <summary>
    /// Creates a path from an exact Windows UTF-16 path value.
    /// </summary>
    /// <param name="path">The nonempty native Windows path.</param>
    /// <returns>A path retaining the supplied immutable string.</returns>
    internal static GitPath FromWindowsPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A Git path cannot contain NUL.", nameof(path));
        }

        return new GitPath(path);
    }

    /// <summary>
    /// Gets the exact Unix bytes retained by this path.
    /// </summary>
    /// <returns>A read-only span over the path-owned byte storage.</returns>
    /// <exception cref="InvalidOperationException">This path contains a Windows representation.</exception>
    internal ReadOnlySpan<byte> GetUnixBytes()
    {
        if (Kind != NativePathKind.UnixBytes)
        {
            throw new InvalidOperationException("The Git path does not contain Unix bytes.");
        }

        return _unixBytes;
    }

    /// <summary>
    /// Gets the exact Windows UTF-16 value retained by this path.
    /// </summary>
    /// <returns>The native Windows path string.</returns>
    /// <exception cref="InvalidOperationException">This path contains a Unix representation.</exception>
    internal string GetWindowsPath()
    {
        if (Kind != NativePathKind.WindowsUtf16)
        {
            throw new InvalidOperationException("The Git path does not contain a Windows path.");
        }

        return _windowsPath!;
    }

    /// <inheritdoc />
    public bool Equals(GitPath? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || Kind != other.Kind)
        {
            return false;
        }

        return Kind == NativePathKind.UnixBytes
            ? _unixBytes!.AsSpan().SequenceEqual(other._unixBytes)
            : string.Equals(_windowsPath, other._windowsPath, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is GitPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        if (Kind == NativePathKind.UnixBytes)
        {
            foreach (var value in _unixBytes!)
            {
                hash.Add(value);
            }
        }
        else
        {
            hash.Add(_windowsPath, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public int CompareTo(GitPath? other)
    {
        if (other is null)
        {
            return 1;
        }

        var kindComparison = Kind.CompareTo(other.Kind);
        if (kindComparison != 0)
        {
            return kindComparison;
        }

        return Kind == NativePathKind.UnixBytes
            ? _unixBytes!.AsSpan().SequenceCompareTo(other._unixBytes)
            : string.Compare(_windowsPath, other._windowsPath, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override string ToString()
        => DisplayText;

    private static string FormatUnixBytes(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        while (!bytes.IsEmpty)
        {
            if (bytes[0] < 0x80)
            {
                AppendAsciiByte(builder, bytes[0]);
                bytes = bytes[1..];
                continue;
            }

            var status = Rune.DecodeFromUtf8(bytes, out var rune, out var consumed);
            if (status != OperationStatus.Done || !IsDisplayRune(rune))
            {
                if (status == OperationStatus.Done)
                {
                    AppendUnicodeToken(builder, rune);
                    bytes = bytes[consumed..];
                }
                else
                {
                    AppendByteToken(builder, bytes[0]);
                    bytes = bytes[1..];
                }

                continue;
            }

            builder.Append(rune);
            bytes = bytes[consumed..];
        }

        return builder.ToString();
    }

    private static string FormatWindowsText(string path)
    {
        var builder = new StringBuilder(path.Length);
        foreach (var rune in path.EnumerateRunes())
        {
            if (rune.IsAscii)
            {
                AppendAsciiByte(builder, (byte)rune.Value);
            }
            else if (IsDisplayRune(rune))
            {
                builder.Append(rune);
            }
            else
            {
                AppendUnicodeToken(builder, rune);
            }
        }

        return builder.ToString();
    }

    private static bool IsDisplayRune(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is not UnicodeCategory.Control and
            not UnicodeCategory.Format and
            not UnicodeCategory.LineSeparator and
            not UnicodeCategory.ParagraphSeparator;
    }

    private static void AppendAsciiByte(StringBuilder builder, byte value)
    {
        if (value is >= 0x20 and <= 0x7e)
        {
            builder.Append((char)value);
        }
        else
        {
            AppendByteToken(builder, value);
        }
    }

    private static void AppendByteToken(StringBuilder builder, byte value)
        => builder.Append("<0x").Append(value.ToString("X2", CultureInfo.InvariantCulture)).Append('>');

    private static void AppendUnicodeToken(StringBuilder builder, Rune rune)
        => builder.Append("<U+").Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture)).Append('>');
}
