using GitSail.Domain;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Represents an existing canonical absolute directory used as a child working directory.
/// </summary>
internal sealed record CanonicalDirectory
{
    private const int UnixOpenReadOnly = 0;
    private const int MaximumUnixPathBytes = 1024 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly byte[]? _unixPath;
    private readonly string? _windowsPath;

    private CanonicalDirectory(byte[] unixPath)
    {
        _unixPath = unixPath;
        Kind = NativePathKind.UnixBytes;
    }

    private CanonicalDirectory(string windowsPath)
    {
        _windowsPath = windowsPath;
        Kind = NativePathKind.WindowsUtf16;
    }

    /// <summary>
    /// Gets the native representation retained by this canonical directory.
    /// </summary>
    internal NativePathKind Kind { get; }

    /// <summary>
    /// Resolves and validates an existing directory.
    /// </summary>
    /// <param name="path">The absolute directory path to resolve.</param>
    /// <returns>The canonical directory.</returns>
    internal static CanonicalDirectory Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A child working directory must be absolute.", nameof(path));
        }

        if (!OperatingSystem.IsWindows())
        {
            return new CanonicalDirectory(ResolveUnixPath(s_strictUtf8.GetBytes(path)));
        }

        var information = new DirectoryInfo(path);
        information.Refresh();
        if (!information.Exists)
        {
            throw new DirectoryNotFoundException($"The child working directory does not exist: {path}");
        }

        var target = information.ResolveLinkTarget(returnFinalTarget: true);
        var canonicalPath = Path.GetFullPath(target?.FullName ?? information.FullName);
        return new CanonicalDirectory(canonicalPath);
    }

    /// <summary>
    /// Resolves a discovered native Git path as a canonical child-process directory.
    /// </summary>
    /// <param name="path">The exact discovered directory path.</param>
    /// <returns>The validated canonical directory.</returns>
    internal static CanonicalDirectory Create(GitPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Kind switch
        {
            NativePathKind.UnixBytes when !OperatingSystem.IsWindows() =>
                new CanonicalDirectory(ResolveUnixPath(path.GetUnixBytes())),
            NativePathKind.WindowsUtf16 when OperatingSystem.IsWindows() =>
                Create(path.GetWindowsPath()),
            _ => throw new PlatformNotSupportedException(
                "The native path kind does not match this operating system."),
        };
    }

    /// <summary>
    /// Gets the exact byte path used by the Unix process boundary.
    /// </summary>
    /// <returns>A read-only span over directory-owned canonical bytes.</returns>
    internal ReadOnlySpan<byte> GetUnixBytes()
        => _unixPath ?? throw new PlatformNotSupportedException(
            "The canonical directory does not contain a Unix path.");

    /// <summary>
    /// Gets the exact UTF-16 path used by the Windows process boundary.
    /// </summary>
    /// <returns>The canonical Windows directory path.</returns>
    internal string GetWindowsPath()
        => _windowsPath ?? throw new PlatformNotSupportedException(
            "The canonical directory does not contain a Windows path.");

    /// <inheritdoc />
    public override string ToString()
        => Kind == NativePathKind.WindowsUtf16
            ? GitPath.FromWindowsPath(_windowsPath!).DisplayText
            : GitPath.FromUnixBytes(_unixPath!).DisplayText;

    private static unsafe byte[] ResolveUnixPath(ReadOnlySpan<byte> path)
    {
        if (path.IsEmpty || path[0] != (byte)'/' || path.Contains((byte)0))
        {
            throw new ArgumentException("A Unix child working directory must be an absolute non-NUL path.", nameof(path));
        }

        var terminatedPath = new byte[path.Length + 1];
        path.CopyTo(terminatedPath);
        byte* resolvedPath = null;
        try
        {
            fixed (byte* pathPointer = terminatedPath)
            {
                resolvedPath = UnixNative.RealPath(pathPointer, resolvedPath: null);
            }

            if (resolvedPath is null)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new DirectoryNotFoundException(
                    $"The native child working directory could not be resolved ({error}).");
            }

            var length = 0;
            while (length < MaximumUnixPathBytes && resolvedPath[length] != 0)
            {
                length++;
            }

            if (length == MaximumUnixPathBytes)
            {
                throw new IOException("The canonical Unix working directory exceeds the supported path limit.");
            }

            var canonicalPath = new ReadOnlySpan<byte>(resolvedPath, length).ToArray();
            ValidateUnixDirectory(canonicalPath);
            return canonicalPath;
        }
        finally
        {
            NativeMemory.Free(resolvedPath);
        }
    }

    private static unsafe void ValidateUnixDirectory(ReadOnlySpan<byte> path)
    {
        var terminatedPath = new byte[path.Length + 1];
        path.CopyTo(terminatedPath);
        var flags = UnixOpenReadOnly |
            GetUnixDirectoryFlag() |
            GetUnixCloseOnExecFlag() |
            GetUnixNoFollowFlag();
        fixed (byte* pathPointer = terminatedPath)
        {
            var fileDescriptor = UnixNative.Open(pathPointer, flags);
            if (fileDescriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException(
                    $"The canonical Unix working directory could not be opened ({error}).",
                    new Win32Exception(error));
            }

            using var directory = new SafeFileHandle((nint)fileDescriptor, ownsHandle: true);
        }
    }

    private static int GetUnixCloseOnExecFlag()
        => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;

    private static int GetUnixDirectoryFlag()
        => OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;

    private static int GetUnixNoFollowFlag()
        => OperatingSystem.IsMacOS() ? 0x0100 : 0x00020000;
}
