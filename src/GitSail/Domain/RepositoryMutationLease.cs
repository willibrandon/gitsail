namespace GitSail.Domain;

/// <summary>
/// Owns one exception-safe exclusive repository mutation lease.
/// </summary>
internal sealed class RepositoryMutationLease : IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private int _released;

    /// <summary>
    /// Initializes an acquired lease over a repository mutation semaphore.
    /// </summary>
    /// <param name="semaphore">The acquired semaphore.</param>
    /// <param name="purpose">The typed operation purpose.</param>
    internal RepositoryMutationLease(SemaphoreSlim semaphore, RepositoryMutationPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(semaphore);
        _semaphore = semaphore;
        Purpose = purpose;
    }

    /// <summary>
    /// Gets the typed purpose for which this lease was acquired.
    /// </summary>
    internal RepositoryMutationPurpose Purpose { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _semaphore.Release();
        }

        return ValueTask.CompletedTask;
    }
}
