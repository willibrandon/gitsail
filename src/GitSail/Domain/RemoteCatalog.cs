using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains one stable complete snapshot of configured Git remotes.
/// </summary>
internal sealed class RemoteCatalog
{
    /// <summary>
    /// Initializes an immutable ordered remote catalog.
    /// </summary>
    /// <param name="remotes">The complete exact configured remote records.</param>
    internal RemoteCatalog(ImmutableArray<RemoteInfo> remotes)
    {
        if (remotes.IsDefault)
        {
            throw new ArgumentException("Remotes must be an initialized collection.", nameof(remotes));
        }

        for (var index = 1; index < remotes.Length; index++)
        {
            if (remotes[index - 1].Name.CompareTo(remotes[index].Name) >= 0)
            {
                throw new ArgumentException(
                    "Remote records must be strictly ordered by exact name.",
                    nameof(remotes));
            }
        }

        Remotes = remotes;
    }

    /// <summary>
    /// Gets every configured remote in stable exact-name order.
    /// </summary>
    internal ImmutableArray<RemoteInfo> Remotes { get; }

    /// <summary>
    /// Finds one exact remote name in the complete catalog.
    /// </summary>
    /// <param name="name">The exact configured name to locate.</param>
    /// <returns>The matching remote, or <see langword="null"/> when absent.</returns>
    internal RemoteInfo? Find(RemoteName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Remotes.FirstOrDefault(remote => remote.Name.Equals(name));
    }

    /// <summary>
    /// Determines whether another catalog has byte-identical complete remote configuration.
    /// </summary>
    /// <param name="other">The stable catalog to compare.</param>
    /// <returns><see langword="true"/> when every ordered record matches.</returns>
    internal bool Matches(RemoteCatalog other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Remotes.Length != other.Remotes.Length)
        {
            return false;
        }

        for (var index = 0; index < Remotes.Length; index++)
        {
            if (!Remotes[index].Matches(other.Remotes[index]))
            {
                return false;
            }
        }

        return true;
    }
}
