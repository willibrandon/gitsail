namespace GitSail.ServiceTests;

/// <summary>
/// Provides cross-platform cleanup for test-owned directory trees.
/// </summary>
internal static class TestDirectory
{
    /// <summary>
    /// Deletes an owned test directory after making Windows read-only Git files removable.
    /// </summary>
    /// <param name="path">The absolute owned test-directory path.</param>
    internal static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Directory.Exists(path))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false,
            };
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            var root = new DirectoryInfo(path);
            root.Attributes &= ~FileAttributes.ReadOnly;
        }

        Directory.Delete(path, recursive: true);
    }
}
