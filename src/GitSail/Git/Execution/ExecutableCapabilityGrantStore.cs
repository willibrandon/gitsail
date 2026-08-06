using GitSail.Domain;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Loads and atomically persists exact command hashes in one user-global repository grant.
/// </summary>
internal sealed class ExecutableCapabilityGrantStore
{
    private static readonly UTF8Encoding s_utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly SemaphoreSlim _persistence = new(1, 1);
    private readonly Func<
        GitConfigurationKey,
        GitConfigurationValue,
        CancellationToken,
        Task<GitConfigurationSnapshot>> _persistAsync;
    private readonly Lock _gate = new();
    private ImmutableHashSet<string> _commandHashes =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    private string? _loadError;

    /// <summary>
    /// Initializes one repository grant store from the current complete configuration snapshot.
    /// </summary>
    /// <param name="repositoryId">The lowercase opaque repository identity.</param>
    /// <param name="configuration">The current ordered configuration snapshot.</param>
    /// <param name="persistAsync">Writes the global grant and returns the reloaded snapshot.</param>
    internal ExecutableCapabilityGrantStore(
        string repositoryId,
        GitConfigurationSnapshot configuration,
        Func<
            GitConfigurationKey,
            GitConfigurationValue,
            CancellationToken,
            Task<GitConfigurationSnapshot>> persistAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        if (!ExecutableCapabilityGrantFormat.IsCommandHash(repositoryId))
        {
            throw new ArgumentException(
                "The repository capability identity must be a lowercase SHA-256 value.",
                nameof(repositoryId));
        }

        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(persistAsync);
        ConfigurationKey = GitConfigurationKey.FromBytes(
            s_utf8.GetBytes($"gitsail.trustedrepository.{repositoryId}"));
        _persistAsync = persistAsync;
        Reload(configuration);
    }

    /// <summary>
    /// Gets the exact global configuration key owned by this repository grant store.
    /// </summary>
    internal GitConfigurationKey ConfigurationKey { get; }

    /// <summary>
    /// Gets the fail-closed validation error for the current global grant, when present.
    /// </summary>
    internal string? LoadError
    {
        get
        {
            lock (_gate)
            {
                return _loadError;
            }
        }
    }

    /// <summary>
    /// Determines whether the exact current request already has a persistent grant.
    /// </summary>
    /// <param name="request">The exact executable capability request.</param>
    /// <returns><see langword="true"/> when the request hash is granted.</returns>
    internal bool IsGranted(ExecutableCapabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            return _commandHashes.Contains(request.CommandHash);
        }
    }

    /// <summary>
    /// Replaces in-memory grants from the exact user-global value in a reloaded snapshot.
    /// </summary>
    /// <param name="configuration">The current ordered configuration snapshot.</param>
    internal void Reload(GitConfigurationSnapshot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var entry = configuration.Entries.LastOrDefault(candidate =>
            candidate.Scope == GitConfigurationScope.Global &&
            candidate.Key.Equals(ConfigurationKey));
        ImmutableHashSet<string> hashes;
        string? loadError = null;
        try
        {
            hashes = entry is null
                ? ImmutableHashSet.Create<string>(StringComparer.Ordinal)
                : Parse(entry.Value);
        }
        catch (InvalidDataException exception)
        {
            hashes = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
            loadError = exception.Message;
        }

        lock (_gate)
        {
            _commandHashes = hashes;
            _loadError = loadError;
        }
    }

    /// <summary>
    /// Persists one additional exact command hash at user-global scope without losing peers.
    /// </summary>
    /// <param name="request">The approved exact executable capability request.</param>
    /// <param name="cancellationToken">Signals serialized configuration persistence cancellation.</param>
    /// <returns>A task that completes after Git reloads the persisted grant.</returns>
    internal async Task GrantAsync(
        ExecutableCapabilityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _persistence.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableHashSet<string> updated;
            lock (_gate)
            {
                if (_commandHashes.Contains(request.CommandHash))
                {
                    return;
                }

                if (_commandHashes.Count >= ExecutableCapabilityGrantFormat.MaximumGrantedCommands)
                {
                    throw new InvalidDataException(
                        $"A repository capability grant cannot contain more than {ExecutableCapabilityGrantFormat.MaximumGrantedCommands} commands.");
                }

                updated = _commandHashes.Add(request.CommandHash);
            }

            var value = GitConfigurationValue.FromBytes(
                ExecutableCapabilityGrantFormat.Serialize(updated));
            var reloaded = await _persistAsync(
                ConfigurationKey,
                value,
                cancellationToken).ConfigureAwait(false);
            Reload(reloaded);
            if (!IsGranted(request))
            {
                throw new InvalidDataException(
                    "The persisted executable capability grant was not visible after Git configuration reloaded.");
            }
        }
        finally
        {
            _persistence.Release();
        }
    }

    private static ImmutableHashSet<string> Parse(GitConfigurationValue value)
    {
        string text;
        try
        {
            text = s_utf8.GetString(value.GetBytes());
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The executable capability grant is not valid UTF-8.",
                exception);
        }

        if (!ExecutableCapabilityGrantFormat.TryParse(text, out var hashes, out var error))
        {
            throw new InvalidDataException(error);
        }

        return hashes;
    }
}
