namespace GitSail.Domain;

/// <summary>
/// Performs exact repository-relative operations without converting native Git paths to display text.
/// </summary>
internal static class GitPathOperations
{
    /// <summary>
    /// Normalizes a repository-relative directory while rejecting absolute and parent traversal forms.
    /// </summary>
    /// <param name="directory">The directory supplied through a native path-bearing input.</param>
    /// <returns>The normalized exact directory, or <see langword="null"/> for the repository root.</returns>
    internal static GitPath? NormalizeDirectory(GitPath directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return directory.Kind == NativePathKind.WindowsUtf16
            ? NormalizeWindowsDirectory(directory)
            : NormalizeUnixDirectory(directory);
    }

    /// <summary>
    /// Normalizes one nonempty repository-relative file path while rejecting absolute and parent traversal forms.
    /// </summary>
    /// <param name="path">The file path supplied through a native path-bearing input.</param>
    /// <returns>The normalized exact repository-relative file path.</returns>
    internal static GitPath NormalizeFile(GitPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Kind == NativePathKind.WindowsUtf16
            ? NormalizeWindowsDirectory(path)
            : NormalizeUnixDirectory(path);
        return normalized
            ?? throw new ArgumentException("A repository file path cannot refer to the repository root.", nameof(path));
    }

    /// <summary>
    /// Appends one immediate tree-entry name to an optional repository-relative directory.
    /// </summary>
    /// <param name="directory">The exact parent directory, or <see langword="null"/> for the root.</param>
    /// <param name="name">The exact immediate entry name.</param>
    /// <returns>The exact combined repository-relative path.</returns>
    internal static GitPath Combine(GitPath? directory, GitPath name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (directory is null)
        {
            return name;
        }

        if (directory.Kind != name.Kind)
        {
            throw new ArgumentException("Git paths must use the same native representation.", nameof(name));
        }

        if (name.Kind == NativePathKind.WindowsUtf16)
        {
            var child = name.GetWindowsPath();
            if (child.Contains('/') || child.Contains('\\'))
            {
                throw new ArgumentException("A tree-entry name cannot contain a directory separator.", nameof(name));
            }

            return GitPath.FromWindowsPath(directory.GetWindowsPath().TrimEnd('/', '\\') + "/" + child);
        }

        var childBytes = name.GetUnixBytes();
        if (childBytes.Contains((byte)'/'))
        {
            throw new ArgumentException("A tree-entry name cannot contain a directory separator.", nameof(name));
        }

        var directoryBytes = directory.GetUnixBytes();
        var trimmedLength = directoryBytes.Length;
        while (trimmedLength > 0 && directoryBytes[trimmedLength - 1] == (byte)'/')
        {
            trimmedLength--;
        }

        var combined = new byte[trimmedLength + 1 + childBytes.Length];
        directoryBytes[..trimmedLength].CopyTo(combined);
        combined[trimmedLength] = (byte)'/';
        childBytes.CopyTo(combined.AsSpan(trimmedLength + 1));
        return GitPath.FromUnixBytes(combined);
    }

    private static GitPath? NormalizeWindowsDirectory(GitPath path)
    {
        var value = path.GetWindowsPath();
        if (Path.IsPathRooted(value))
        {
            throw new ArgumentException("A repository path must be relative.", nameof(path));
        }

        var components = value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var retained = new List<string>(components.Length);
        foreach (var component in components)
        {
            if (string.Equals(component, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(component, "..", StringComparison.Ordinal))
            {
                throw new ArgumentException("A repository path cannot contain parent traversal.", nameof(path));
            }

            retained.Add(component);
        }

        return retained.Count == 0
            ? null
            : GitPath.FromWindowsPath(string.Join('/', retained));
    }

    private static GitPath? NormalizeUnixDirectory(GitPath path)
    {
        var value = path.GetUnixBytes();
        if (value[0] == (byte)'/')
        {
            throw new ArgumentException("A repository path must be relative.", nameof(path));
        }

        var normalized = new byte[value.Length];
        var written = 0;
        var offset = 0;
        while (offset <= value.Length)
        {
            var remaining = value[offset..];
            var separator = remaining.IndexOf((byte)'/');
            var componentLength = separator < 0 ? remaining.Length : separator;
            var component = remaining[..componentLength];
            if (!component.IsEmpty && !component.SequenceEqual("."u8))
            {
                if (component.SequenceEqual(".."u8))
                {
                    throw new ArgumentException("A repository path cannot contain parent traversal.", nameof(path));
                }

                if (written > 0)
                {
                    normalized[written++] = (byte)'/';
                }

                component.CopyTo(normalized.AsSpan(written));
                written += component.Length;
            }

            if (separator < 0)
            {
                break;
            }

            offset += componentLength + 1;
        }

        return written == 0 ? null : GitPath.FromUnixBytes(normalized.AsSpan(0, written));
    }
}
