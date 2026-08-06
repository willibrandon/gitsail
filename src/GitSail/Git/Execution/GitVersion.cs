using System.Globalization;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Represents a parsed Git semantic version and any vendor suffix.
/// </summary>
internal readonly record struct GitVersion : IComparable<GitVersion>
{
    /// <summary>
    /// Gets the oldest Git version supported by every GitSail command contract.
    /// Newer optional features remain guarded by their individual capability checks.
    /// </summary>
    internal static GitVersion MinimumSupported { get; } = new(2, 36, 0, string.Empty);

    private GitVersion(int major, int minor, int patch, string suffix)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Suffix = suffix;
    }

    /// <summary>
    /// Gets the major version component.
    /// </summary>
    internal int Major { get; }

    /// <summary>
    /// Gets the minor version component.
    /// </summary>
    internal int Minor { get; }

    /// <summary>
    /// Gets the patch version component, or zero when Git omitted it.
    /// </summary>
    internal int Patch { get; }

    /// <summary>
    /// Gets the vendor or build suffix after the numeric version.
    /// </summary>
    internal string Suffix { get; }

    /// <summary>
    /// Parses the byte output from <c>git --version</c> without culture-sensitive conversion.
    /// </summary>
    /// <param name="bytes">The complete standard-output byte sequence.</param>
    /// <param name="version">The parsed Git version when successful.</param>
    /// <returns><see langword="true"/> when the output has the documented Git version shape.</returns>
    internal static bool TryParse(ReadOnlySpan<byte> bytes, out GitVersion version)
    {
        version = default;
        bytes = TrimAsciiWhitespace(bytes);
        ReadOnlySpan<byte> prefix = "git version "u8;
        if (!bytes.StartsWith(prefix))
        {
            return false;
        }

        bytes = bytes[prefix.Length..];
        if (!TryReadNumber(ref bytes, out var major) || !TryConsume(ref bytes, (byte)'.') ||
            !TryReadNumber(ref bytes, out var minor))
        {
            return false;
        }

        var patch = 0;
        if (TryConsume(ref bytes, (byte)'.') && !TryReadNumber(ref bytes, out patch))
        {
            return false;
        }

        if (!bytes.IsEmpty && bytes[0] is not ((byte)' ' or (byte)'-' or (byte)'.'))
        {
            return false;
        }

        var suffix = Encoding.UTF8.GetString(bytes).Trim();
        version = new GitVersion(major, minor, patch, suffix);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(GitVersion other)
    {
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Minor.CompareTo(other.Minor);
        return comparison != 0 ? comparison : Patch.CompareTo(other.Patch);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var numeric = string.Create(
            CultureInfo.InvariantCulture,
            $"{Major}.{Minor}.{Patch}");
        if (string.IsNullOrEmpty(Suffix))
        {
            return numeric;
        }

        return Suffix[0] is '.' or '-'
            ? numeric + Suffix
            : $"{numeric} {Suffix}";
    }

    private static bool TryConsume(ref ReadOnlySpan<byte> bytes, byte expected)
    {
        if (bytes.IsEmpty || bytes[0] != expected)
        {
            return false;
        }

        bytes = bytes[1..];
        return true;
    }

    private static bool TryReadNumber(ref ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        var length = 0;
        while (length < bytes.Length && bytes[length] is >= (byte)'0' and <= (byte)'9')
        {
            var digit = bytes[length] - (byte)'0';
            if (value > (int.MaxValue - digit) / 10)
            {
                return false;
            }

            value = (value * 10) + digit;
            length++;
        }

        if (length == 0)
        {
            return false;
        }

        bytes = bytes[length..];
        return true;
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty && IsAsciiWhitespace(bytes[0]))
        {
            bytes = bytes[1..];
        }

        while (!bytes.IsEmpty && IsAsciiWhitespace(bytes[^1]))
        {
            bytes = bytes[..^1];
        }

        return bytes;
    }

    private static bool IsAsciiWhitespace(byte value)
        => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
