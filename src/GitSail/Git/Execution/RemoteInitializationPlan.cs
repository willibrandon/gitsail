using GitSail.Domain;

namespace GitSail.Git.Execution;

/// <summary>
/// Binds one selected configured URL to its exact effective typed initialization target.
/// </summary>
internal sealed class RemoteInitializationPlan
{
    /// <summary>
    /// Initializes one immutable local or SSH remote-initialization confirmation.
    /// </summary>
    /// <param name="catalog">The complete exact remote catalog displayed to the user.</param>
    /// <param name="remote">The exact selected configured remote.</param>
    /// <param name="configuredUrlIndex">The selected configured push-URL index.</param>
    /// <param name="target">The exact effective typed initialization target.</param>
    /// <param name="objectFormat">The local repository object format requested for the new bare repository.</param>
    /// <param name="sshExecutable">The resolved SSH executable for an SSH target.</param>
    /// <param name="sshDecoder">The verified remote decoder for an SSH target.</param>
    internal RemoteInitializationPlan(
        RemoteCatalog catalog,
        RemoteInfo remote,
        int configuredUrlIndex,
        RemoteInitializationTarget target,
        RepositoryObjectFormat objectFormat,
        ResolvedExecutable? sshExecutable,
        SshBase64Decoder? sshDecoder)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(target);
        if (configuredUrlIndex < 0 || configuredUrlIndex >= remote.PushUrls.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(configuredUrlIndex));
        }

        if (!Enum.IsDefined(objectFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(objectFormat));
        }

        if (target.Kind == RemoteInitializationKind.Ssh)
        {
            ArgumentNullException.ThrowIfNull(sshExecutable);
            if (sshExecutable.Kind != ProgramKind.Ssh || sshDecoder is null || !Enum.IsDefined(sshDecoder.Value))
            {
                throw new ArgumentException("An SSH initialization plan requires one verified SSH capability.");
            }
        }
        else if (sshExecutable is not null || sshDecoder is not null)
        {
            throw new ArgumentException("A local initialization plan cannot contain SSH capability data.");
        }

        var catalogRemote = catalog.Find(remote.Name);
        if (catalogRemote is null || !catalogRemote.Matches(remote))
        {
            throw new ArgumentException("The selected remote must be an exact member of the bound catalog.");
        }

        Catalog = catalog;
        Remote = remote;
        ConfiguredUrlIndex = configuredUrlIndex;
        Target = target;
        ObjectFormat = objectFormat;
        SshExecutable = sshExecutable;
        SshDecoder = sshDecoder;
    }

    /// <summary>
    /// Gets the complete exact remote catalog bound to this plan.
    /// </summary>
    internal RemoteCatalog Catalog { get; }

    /// <summary>
    /// Gets the exact selected configured remote.
    /// </summary>
    internal RemoteInfo Remote { get; }

    /// <summary>
    /// Gets the selected configured push-URL index.
    /// </summary>
    internal int ConfiguredUrlIndex { get; }

    /// <summary>
    /// Gets the selected configured push URL before Git URL rewriting.
    /// </summary>
    internal RemoteUrl ConfiguredUrl => Remote.PushUrls[ConfiguredUrlIndex];

    /// <summary>
    /// Gets the exact effective local or SSH target.
    /// </summary>
    internal RemoteInitializationTarget Target { get; }

    /// <summary>
    /// Gets the requested Git object format for the new bare repository.
    /// </summary>
    internal RepositoryObjectFormat ObjectFormat { get; }

    /// <summary>
    /// Gets the exact resolved SSH executable when required.
    /// </summary>
    internal ResolvedExecutable? SshExecutable { get; }

    /// <summary>
    /// Gets the verified remote base64 decoder when required.
    /// </summary>
    internal SshBase64Decoder? SshDecoder { get; }
}
