using GitSail.Domain;

namespace GitSail.Git.Execution;

/// <summary>
/// Resolves validated repository-relative Git paths beneath one canonical worktree root.
/// </summary>
internal static class RepositoryWorkTreePathService
{
    /// <summary>
    /// Combines one canonical worktree root and repository-relative path without lossy Unix decoding.
    /// </summary>
    /// <param name="repository">The discovered repository containing a non-null worktree root.</param>
    /// <param name="relativePath">The exact repository-relative path reported by Git.</param>
    /// <returns>The exact absolute native path beneath the worktree.</returns>
    internal static GitPath Resolve(RepositoryLocation repository, GitPath relativePath)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(relativePath);
        var workTree = repository.WorkTree
            ?? throw new InvalidOperationException("A bare repository has no worktree path.");
        if (workTree.Kind != relativePath.Kind)
        {
            throw new InvalidDataException("The worktree and relative path use different native representations.");
        }

        return workTree.Kind == NativePathKind.WindowsUtf16
            ? ResolveWindows(workTree, relativePath)
            : ResolveUnix(workTree, relativePath);
    }

    private static GitPath ResolveWindows(GitPath workTree, GitPath relativePath)
    {
        var relative = relativePath.GetWindowsPath().Replace('/', '\\');
        ValidateWindowsRelativePath(relative);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workTree.GetWindowsPath()));
        var combined = Path.GetFullPath(Path.Combine(root, relative));
        var requiredPrefix = root + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A Git path escaped the canonical worktree root.");
        }

        return GitPath.FromWindowsPath(combined);
    }

    private static GitPath ResolveUnix(GitPath workTree, GitPath relativePath)
    {
        var root = workTree.GetUnixBytes();
        var relative = relativePath.GetUnixBytes();
        ValidateUnixRelativePath(relative);
        var needsSeparator = root.Length > 1 && root[^1] != (byte)'/';
        var result = new byte[checked(root.Length + (needsSeparator ? 1 : 0) + relative.Length)];
        root.CopyTo(result);
        var offset = root.Length;
        if (needsSeparator)
        {
            result[offset++] = (byte)'/';
        }

        relative.CopyTo(result.AsSpan(offset));
        return GitPath.FromUnixBytes(result);
    }

    private static void ValidateWindowsRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathFullyQualified(path) || Path.GetPathRoot(path)?.Length > 0)
        {
            throw new InvalidDataException("A Windows Git worktree path must be nonempty and relative.");
        }

        foreach (var component in path.Split('\\'))
        {
            if (component.Length == 0 || component is "." or ".." ||
                component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException("A Windows Git worktree path contains an unsafe component.");
            }
        }
    }

    private static void ValidateUnixRelativePath(ReadOnlySpan<byte> path)
    {
        if (path.IsEmpty || path[0] == (byte)'/')
        {
            throw new InvalidDataException("A Unix Git worktree path must be nonempty and relative.");
        }

        var offset = 0;
        while (offset <= path.Length)
        {
            var separator = path[offset..].IndexOf((byte)'/');
            var end = separator < 0 ? path.Length : offset + separator;
            var component = path[offset..end];
            if (component.IsEmpty || component.SequenceEqual("."u8) || component.SequenceEqual(".."u8))
            {
                throw new InvalidDataException("A Unix Git worktree path contains an unsafe component.");
            }

            if (separator < 0)
            {
                break;
            }

            offset = end + 1;
        }
    }
}
