using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Represents one literal child-process argument that is never interpreted by a shell.
/// </summary>
internal sealed class ProcessArgument
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly string? _managedValue;
    private readonly byte[]? _unixBytes;

    private ProcessArgument(string managedValue)
    {
        _managedValue = managedValue;
    }

    private ProcessArgument(byte[] unixBytes)
    {
        _unixBytes = unixBytes;
    }

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

    /// <summary>
    /// Creates one literal argument from exact native Unix bytes.
    /// </summary>
    /// <param name="value">The non-NUL native argument bytes.</param>
    /// <returns>The typed literal argument that owns a copy of the supplied bytes.</returns>
    internal static ProcessArgument FromUnixBytes(ReadOnlySpan<byte> value)
    {
        if (value.Contains((byte)0))
        {
            throw new ArgumentException("A process argument cannot contain NUL.", nameof(value));
        }

        return new ProcessArgument(value.ToArray());
    }

    /// <summary>
    /// Creates one native argument from an exact platform path.
    /// </summary>
    /// <param name="path">The exact platform path.</param>
    /// <returns>The typed native argument.</returns>
    internal static ProcessArgument Native(GitPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Kind switch
        {
            NativePathKind.UnixBytes when !OperatingSystem.IsWindows() =>
                FromUnixBytes(path.GetUnixBytes()),
            NativePathKind.WindowsUtf16 when OperatingSystem.IsWindows() =>
                Literal(path.GetWindowsPath()),
            _ => throw new PlatformNotSupportedException(
                "The native path kind does not match this operating system."),
        };
    }

    /// <summary>
    /// Creates one native argument from an exact Git reference name.
    /// </summary>
    /// <param name="referenceName">The exact Git reference bytes.</param>
    /// <returns>The typed native argument.</returns>
    internal static ProcessArgument Native(RefName referenceName)
    {
        ArgumentNullException.ThrowIfNull(referenceName);
        return OperatingSystem.IsWindows()
            ? Literal(s_strictUtf8.GetString(referenceName.GetBytes()))
            : FromUnixBytes(referenceName.GetBytes());
    }

    /// <summary>
    /// Creates one native argument from an exact Git remote name.
    /// </summary>
    /// <param name="remoteName">The exact Git remote-name bytes.</param>
    /// <returns>The typed native argument.</returns>
    internal static ProcessArgument Native(RemoteName remoteName)
    {
        ArgumentNullException.ThrowIfNull(remoteName);
        return OperatingSystem.IsWindows()
            ? Literal(s_strictUtf8.GetString(remoteName.GetBytes()))
            : FromUnixBytes(remoteName.GetBytes());
    }

    /// <summary>
    /// Creates one native argument from an exact Git remote URL.
    /// </summary>
    /// <param name="remoteUrl">The exact Git remote-URL bytes.</param>
    /// <returns>The typed native argument.</returns>
    internal static ProcessArgument Native(RemoteUrl remoteUrl)
    {
        ArgumentNullException.ThrowIfNull(remoteUrl);
        return OperatingSystem.IsWindows()
            ? Literal(s_strictUtf8.GetString(remoteUrl.GetBytes()))
            : FromUnixBytes(remoteUrl.GetBytes());
    }

    /// <summary>
    /// Creates one native argument from an exact canonical Git configuration key.
    /// </summary>
    /// <param name="configurationKey">The exact canonical configuration-key bytes.</param>
    /// <returns>The typed native argument.</returns>
    internal static ProcessArgument Native(GitConfigurationKey configurationKey)
    {
        ArgumentNullException.ThrowIfNull(configurationKey);
        return OperatingSystem.IsWindows()
            ? Literal(s_strictUtf8.GetString(configurationKey.GetBytes()))
            : FromUnixBytes(configurationKey.GetBytes());
    }

    /// <summary>
    /// Gets the exact UTF-16 argument used by the Windows process boundary.
    /// </summary>
    /// <returns>The native Windows argument.</returns>
    internal string GetWindowsValue()
        => _managedValue ?? throw new PlatformNotSupportedException(
            "A Unix byte argument cannot be represented by the Windows process boundary.");

    /// <summary>
    /// Gets the exact byte argument used by the Unix process boundary.
    /// </summary>
    /// <returns>A read-only span over the argument-owned byte storage.</returns>
    internal ReadOnlySpan<byte> GetUnixBytes()
        => _unixBytes ?? s_strictUtf8.GetBytes(_managedValue!);

    /// <summary>
    /// Determines whether this argument is the specified managed literal.
    /// </summary>
    /// <param name="value">The literal value to compare.</param>
    /// <returns><see langword="true"/> when the values are identical.</returns>
    internal bool IsLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Equals(_managedValue, value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override string ToString()
        => _managedValue ?? GitPath.FromUnixBytes(_unixBytes!).DisplayText;
}
