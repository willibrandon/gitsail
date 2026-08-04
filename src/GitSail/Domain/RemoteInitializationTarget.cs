namespace GitSail.Domain;

/// <summary>
/// Describes one typed local-path or SSH remote repository initialization target.
/// </summary>
internal sealed class RemoteInitializationTarget
{
    /// <summary>
    /// Initializes one validated typed target with transport-specific data.
    /// </summary>
    /// <param name="url">The exact effective remote URL.</param>
    /// <param name="kind">The selected local or SSH transport.</param>
    /// <param name="localPath">The canonicalizable absolute local path, when local.</param>
    /// <param name="sshDestination">The structured destination, when SSH.</param>
    /// <param name="sshPort">The optional validated SSH port.</param>
    /// <param name="remotePath">The exact non-NUL remote path bytes, when SSH.</param>
    internal RemoteInitializationTarget(
        RemoteUrl url,
        RemoteInitializationKind kind,
        string? localPath,
        SshDestination? sshDestination,
        int? sshPort,
        byte[]? remotePath)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == RemoteInitializationKind.Local)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
            if (sshDestination is not null || sshPort is not null || remotePath is not null)
            {
                throw new ArgumentException("A local initialization target cannot contain SSH data.");
            }
        }
        else
        {
            ArgumentNullException.ThrowIfNull(sshDestination);
            if (localPath is not null || remotePath is null || remotePath.Length == 0 || remotePath.Contains((byte)0))
            {
                throw new ArgumentException("An SSH initialization target requires one exact non-NUL remote path.");
            }

            if (sshPort is <= 0 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(sshPort));
            }
        }

        Url = url;
        Kind = kind;
        LocalPath = localPath;
        SshDestination = sshDestination;
        SshPort = sshPort;
        RemotePath = remotePath;
    }

    /// <summary>
    /// Gets the exact effective URL represented by this target.
    /// </summary>
    internal RemoteUrl Url { get; }

    /// <summary>
    /// Gets the local or SSH initialization transport kind.
    /// </summary>
    internal RemoteInitializationKind Kind { get; }

    /// <summary>
    /// Gets the absolute local path when this is a local target.
    /// </summary>
    internal string? LocalPath { get; }

    /// <summary>
    /// Gets the structured SSH destination when this is an SSH target.
    /// </summary>
    internal SshDestination? SshDestination { get; }

    /// <summary>
    /// Gets the explicit SSH port when one was supplied.
    /// </summary>
    internal int? SshPort { get; }

    /// <summary>
    /// Gets the exact remote repository path bytes when this is an SSH target.
    /// </summary>
    internal byte[]? RemotePath { get; }
}
