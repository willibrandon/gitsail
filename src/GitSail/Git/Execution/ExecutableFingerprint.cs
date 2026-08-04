namespace GitSail.Git.Execution;

/// <summary>
/// Captures stable file metadata used to detect executable replacement before launch.
/// </summary>
/// <param name="Length">The executable length in bytes.</param>
/// <param name="LastWriteTimeUtcTicks">The executable's last-write timestamp in UTC ticks.</param>
internal readonly record struct ExecutableFingerprint(long Length, long LastWriteTimeUtcTicks)
{
    /// <summary>
    /// Captures the current fingerprint for an executable file.
    /// </summary>
    /// <param name="path">The canonical executable path.</param>
    /// <returns>The captured executable fingerprint.</returns>
    internal static ExecutableFingerprint Capture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var information = new FileInfo(path);
        information.Refresh();
        if (!information.Exists || (information.Attributes & FileAttributes.Directory) != 0)
        {
            throw new FileNotFoundException("The resolved executable is no longer a regular file.", path);
        }

        return new ExecutableFingerprint(information.Length, information.LastWriteTimeUtc.Ticks);
    }
}
