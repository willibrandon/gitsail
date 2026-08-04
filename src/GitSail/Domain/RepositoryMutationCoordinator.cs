namespace GitSail.Domain;

/// <summary>
/// Serializes index and repository mutations for one active repository session.
/// </summary>
internal sealed class RepositoryMutationCoordinator : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// Acquires an exception-safe exclusive lease for one typed operation purpose.
    /// </summary>
    /// <param name="purpose">The repository operation purpose.</param>
    /// <param name="cancellationToken">Signals cancellation while waiting for the lease.</param>
    /// <returns>The acquired lease.</returns>
    internal async ValueTask<RepositoryMutationLease> AcquireAsync(
        RepositoryMutationPurpose purpose,
        CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new RepositoryMutationLease(_semaphore, purpose);
    }

    /// <inheritdoc />
    public void Dispose()
        => _semaphore.Dispose();
}
