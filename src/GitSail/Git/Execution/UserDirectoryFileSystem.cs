namespace GitSail.Git.Execution;

/// <summary>
/// Creates and validates application-owned user directories without accepting reparse indirection.
/// </summary>
internal static class UserDirectoryFileSystem
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates one application-owned directory and enforces user-only Unix permissions.
    /// </summary>
    /// <param name="path">The fully qualified application-owned directory path.</param>
    internal static void EnsurePrivateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A private user directory must be fully qualified.", nameof(path));
        }

        var directory = new DirectoryInfo(path);
        directory.Refresh();
        if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("A private user directory cannot be a symbolic link or reparse point.");
        }

        if (!directory.Exists)
        {
            if (OperatingSystem.IsWindows())
            {
                directory.Create();
            }
            else
            {
                _ = Directory.CreateDirectory(path, PrivateDirectoryMode);
            }

            directory.Refresh();
        }

        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The private user directory could not be created safely.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
    }
}
