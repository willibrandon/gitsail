using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GitSail.Git.Execution;

/// <summary>
/// Deletes only the unchanged directory and parent identities captured after a failed creation.
/// </summary>
internal sealed class CreatedDirectoryCleanup
{
    private const uint WindowsFileReadAttributes = 0x00000080;
    private const uint WindowsShareRead = 0x00000001;
    private const uint WindowsShareWrite = 0x00000002;
    private const uint WindowsShareDelete = 0x00000004;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsFileAttributeDirectory = 0x00000010;
    private const uint WindowsFileAttributeReparsePoint = 0x00000400;
    private const uint WindowsFileFlagOpenReparsePoint = 0x00200000;
    private const uint WindowsFileFlagBackupSemantics = 0x02000000;
    private const int WindowsFileAttributeTagInfo = 9;
    private const int WindowsFileIdInfo = 18;
    private const int WindowsFileIdInfoBytes = 24;
    private readonly string _parentPath;
    private readonly byte[] _parentIdentity;
    private readonly string _targetPath;
    private readonly byte[] _targetIdentity;
    private int _deleted;

    private CreatedDirectoryCleanup(
        string parentPath,
        byte[] parentIdentity,
        string targetPath,
        byte[] targetIdentity)
    {
        _parentPath = parentPath;
        _parentIdentity = parentIdentity;
        _targetPath = targetPath;
        _targetIdentity = targetIdentity;
    }

    /// <summary>
    /// Gets the control-safe target path shown by the cleanup confirmation.
    /// </summary>
    internal string DisplayPath => OperatingSystem.IsWindows()
        ? Domain.GitPath.FromWindowsPath(_targetPath).DisplayText
        : Domain.GitPath.FromUnixBytes(System.Text.Encoding.UTF8.GetBytes(_targetPath)).DisplayText;

    /// <summary>
    /// Captures a cleanup offer only for an absent-before-operation directory now present under the same parent.
    /// </summary>
    /// <param name="plan">The canonical target plan captured before Git was launched.</param>
    /// <returns>An identity-checked cleanup offer, or <see langword="null"/> when the target is ineligible.</returns>
    internal static CreatedDirectoryCleanup? Capture(RepositoryTargetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.ExistedBeforeOperation || !Directory.Exists(plan.ManagedTargetPath))
        {
            return null;
        }

        var parentPath = Path.GetDirectoryName(plan.ManagedTargetPath)
            ?? throw new InvalidDataException("A repository cleanup target has no parent directory.");
        var expectedParent = GetManagedPath(plan.ParentDirectory);
        if (!PathEquals(parentPath, expectedParent))
        {
            throw new InvalidDataException("A repository cleanup target changed canonical parent.");
        }

        return new CreatedDirectoryCleanup(
            parentPath,
            CaptureDirectoryIdentity(parentPath),
            plan.ManagedTargetPath,
            CaptureDirectoryIdentity(plan.ManagedTargetPath));
    }

    /// <summary>
    /// Deletes the captured tree only while its parent and target retain their exact no-follow identities.
    /// </summary>
    /// <param name="cancellationToken">Signals cancellation before destructive work begins.</param>
    /// <returns>A task that completes after the exact captured tree is removed.</returns>
    internal Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _deleted, 1) != 0)
        {
            throw new InvalidOperationException("The repository cleanup offer was already consumed.");
        }

        try
        {
            if (!CaptureDirectoryIdentity(_parentPath).AsSpan().SequenceEqual(_parentIdentity))
            {
                throw new IOException("The repository cleanup parent identity changed.");
            }

            if (!CaptureDirectoryIdentity(_targetPath).AsSpan().SequenceEqual(_targetIdentity))
            {
                throw new IOException("The repository cleanup target identity changed.");
            }

            DeleteTreeWithoutFollowingLinks(_targetPath, cancellationToken);
            return Task.CompletedTask;
        }
        catch
        {
            Volatile.Write(ref _deleted, 0);
            throw;
        }
    }

    private static byte[] CaptureDirectoryIdentity(string path)
        => OperatingSystem.IsWindows()
            ? CaptureWindowsDirectoryIdentity(path)
            : CaptureUnixDirectoryIdentity(path);

    private static unsafe byte[] CaptureWindowsDirectoryIdentity(string path)
    {
        using var directory = WindowsNative.CreateFile(
            path,
            WindowsFileReadAttributes,
            WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
            securityAttributes: 0,
            WindowsOpenExisting,
            WindowsFileFlagOpenReparsePoint | WindowsFileFlagBackupSemantics,
            templateFile: 0);
        if (directory.IsInvalid)
        {
            throw CreateNativeIOException("The repository cleanup directory could not be opened.");
        }

        if (WindowsNative.GetFileInformationByHandleEx(
                directory,
                WindowsFileAttributeTagInfo,
                out var attributes,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()) == 0)
        {
            throw CreateNativeIOException("The repository cleanup directory attributes could not be read.");
        }

        if ((attributes._fileAttributes & WindowsFileAttributeDirectory) == 0 ||
            (attributes._fileAttributes & WindowsFileAttributeReparsePoint) != 0)
        {
            throw new IOException("A repository cleanup path is not a no-follow directory.");
        }

        var identity = new byte[WindowsFileIdInfoBytes];
        fixed (byte* identityPointer = identity)
        {
            if (WindowsNative.GetFileInformationByHandleEx(
                    directory,
                    WindowsFileIdInfo,
                    identityPointer,
                    (uint)identity.Length) == 0)
            {
                throw CreateNativeIOException("The repository cleanup directory identity could not be read.");
            }
        }

        return identity;
    }

    private static byte[] CaptureUnixDirectoryIdentity(string path)
    {
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(path);
        var terminatedPath = new byte[pathBytes.Length + 1];
        pathBytes.CopyTo(terminatedPath, 0);
        using var directory = UnixFileHandle.OpenDirectory(
            terminatedPath,
            "The repository cleanup directory could not be opened.");
        var status = UnixFileHandle.GetStatus(
            directory,
            "The repository cleanup directory identity could not be read.");
        var identity = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(identity, unchecked((ulong)status.Device));
        BinaryPrimitives.WriteUInt64LittleEndian(
            identity.AsSpan(8),
            unchecked((ulong)status.Inode));
        return identity;
    }

    private static void DeleteTreeWithoutFollowingLinks(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(entry);
                }
                else
                {
                    DeleteTreeWithoutFollowingLinks(entry, cancellationToken);
                }
            }
            else
            {
                if (OperatingSystem.IsWindows() && (attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                }

                File.Delete(entry);
            }
        }

        Directory.Delete(directoryPath);
    }

    private static string GetManagedPath(CanonicalDirectory directory)
        => directory.Kind == Domain.NativePathKind.WindowsUtf16
            ? directory.GetWindowsPath()
            : System.Text.Encoding.UTF8.GetString(directory.GetUnixBytes());

    private static bool PathEquals(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static IOException CreateNativeIOException(string message)
    {
        var error = Marshal.GetLastPInvokeError();
        return new IOException($"{message} ({error}).", new Win32Exception(error));
    }
}
