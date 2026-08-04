using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Builds exact NUL-delimited literal Git pathspec input without using managed argv.
/// </summary>
internal static class PathspecInputBuilder
{
    private const int MaximumInputBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Builds a nonempty bounded NUL-delimited pathspec byte sequence.
    /// </summary>
    /// <param name="paths">The exact native paths selected for one operation.</param>
    /// <returns>The owned NUL-delimited input bytes.</returns>
    internal static byte[] Build(IReadOnlyCollection<GitPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("At least one Git path is required.", nameof(paths));
        }

        var encodedPaths = new List<byte[]>(paths.Count);
        var totalLength = 0;
        foreach (var path in paths)
        {
            ArgumentNullException.ThrowIfNull(path);
            var bytes = GetPlatformBytes(path);
            totalLength = checked(totalLength + bytes.Length + 1);
            if (totalLength > MaximumInputBytes)
            {
                throw new ArgumentException("The selected pathspec input exceeds the configured limit.", nameof(paths));
            }

            encodedPaths.Add(bytes);
        }

        var result = new byte[totalLength];
        var offset = 0;
        foreach (var path in encodedPaths)
        {
            path.CopyTo(result, offset);
            offset += path.Length + 1;
        }

        return result;
    }

    private static byte[] GetPlatformBytes(GitPath path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (path.Kind != NativePathKind.WindowsUtf16)
            {
                throw new ArgumentException("A Windows Git operation requires a Windows path representation.", nameof(path));
            }

            return Encoding.UTF8.GetBytes(path.GetWindowsPath());
        }

        if (path.Kind != NativePathKind.UnixBytes)
        {
            throw new ArgumentException("A Unix Git operation requires a Unix byte path representation.", nameof(path));
        }

        return path.GetUnixBytes().ToArray();
    }
}
