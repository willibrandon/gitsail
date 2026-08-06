using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Debounces and durably persists one recoverable commit-message draft and its previous revision.
/// </summary>
internal sealed class CommitDraftStore : IAsyncDisposable
{
    private const int MaximumCommitDraftBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly GitPath _messagePath;
    private readonly GitPath _backupPath;
    private readonly OperationSupervisor _operationSupervisor;
    private readonly TimeSpan _autosaveDelay;
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private CancellationTokenSource? _scheduledSaveCancellation;
    private string _latestMessage;
    private long _version;
    private bool _isDirty;
    private bool _isDisposed;

    /// <summary>
    /// Initializes recoverable draft persistence over exact allowlisted native paths.
    /// </summary>
    /// <param name="messagePath">The exact primary recoverable-draft path.</param>
    /// <param name="backupPath">The exact previous-revision backup path.</param>
    /// <param name="operationSupervisor">The repository-session owner for delayed persistence.</param>
    /// <param name="initialMessage">The empty or recovered editor message.</param>
    /// <param name="autosaveDelay">The nonnegative idle delay before persistence.</param>
    internal CommitDraftStore(
        GitPath messagePath,
        GitPath backupPath,
        OperationSupervisor operationSupervisor,
        string initialMessage,
        TimeSpan autosaveDelay)
    {
        ArgumentNullException.ThrowIfNull(messagePath);
        ArgumentNullException.ThrowIfNull(backupPath);
        ArgumentNullException.ThrowIfNull(operationSupervisor);
        ArgumentNullException.ThrowIfNull(initialMessage);
        ArgumentOutOfRangeException.ThrowIfLessThan(autosaveDelay, TimeSpan.Zero);
        _messagePath = messagePath;
        _backupPath = backupPath;
        _operationSupervisor = operationSupervisor;
        _latestMessage = initialMessage;
        _autosaveDelay = autosaveDelay;
    }

    /// <summary>
    /// Reports a recoverable asynchronous persistence failure to the owning session.
    /// </summary>
    internal event Action<Exception>? PersistenceFailed;

    /// <summary>
    /// Gets the monotonic editor-change version known to the persistence coordinator.
    /// </summary>
    internal long Version
    {
        get
        {
            lock (_sync)
            {
                return _version;
            }
        }
    }

    /// <summary>
    /// Schedules the newest complete editor message after the configured idle delay.
    /// </summary>
    /// <param name="message">The complete editor message at this change version.</param>
    internal void ScheduleSave(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        CancellationTokenSource? previousCancellation;
        CancellationTokenSource currentCancellation;
        long version;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _latestMessage = message;
            _isDirty = true;
            version = checked(++_version);
            previousCancellation = _scheduledSaveCancellation;
            currentCancellation = new CancellationTokenSource();
            _scheduledSaveCancellation = currentCancellation;
        }

        Cancel(previousCancellation);
        _operationSupervisor.Start(
            "commit-draft-autosave",
            context => SaveAfterDelayAsync(
                version,
                message,
                currentCancellation,
                context.CancellationToken),
            currentCancellation.Token);
    }

    /// <summary>
    /// Immediately persists the latest changed draft revision, if one is pending.
    /// </summary>
    /// <param name="cancellationToken">Signals flush cancellation before atomic replacement.</param>
    /// <returns>A task that completes after the latest changed revision is durable.</returns>
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        string message;
        long version;
        bool isDirty;
        CancellationTokenSource? scheduledCancellation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            message = _latestMessage;
            version = _version;
            isDirty = _isDirty;
            scheduledCancellation = _scheduledSaveCancellation;
            _scheduledSaveCancellation = null;
        }

        Cancel(scheduledCancellation);
        if (!isDirty)
        {
            return;
        }

        await PersistVersionAsync(version, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes both recovery files only when no editor change followed the committed revision.
    /// </summary>
    /// <param name="expectedVersion">The draft version captured with the committed message.</param>
    /// <param name="cancellationToken">Signals cancellation before identity-checked deletion.</param>
    /// <returns><see langword="true"/> when the matching recovery state was discarded.</returns>
    internal async Task<bool> TryDiscardAsync(
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? scheduledCancellation = null;
        long discardVersion = 0;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                if (_version != expectedVersion)
                {
                    return false;
                }

                discardVersion = checked(++_version);
                _latestMessage = string.Empty;
                _isDirty = false;
                scheduledCancellation = _scheduledSaveCancellation;
                _scheduledSaveCancellation = null;
            }

            Cancel(scheduledCancellation);
            await DeleteRecoveryFilesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        lock (_sync)
        {
            return _version == discardVersion;
        }
    }

    /// <summary>
    /// Flushes the latest changed draft before ending persistence for this repository session.
    /// </summary>
    /// <returns>A value task that completes after pending recovery state is durable.</returns>
    public async ValueTask DisposeAsync()
    {
        string message;
        long version;
        bool isDirty;
        CancellationTokenSource? scheduledCancellation;
        lock (_sync)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            message = _latestMessage;
            version = _version;
            isDirty = _isDirty;
            scheduledCancellation = _scheduledSaveCancellation;
            _scheduledSaveCancellation = null;
        }

        Cancel(scheduledCancellation);
        if (isDirty)
        {
            await PersistVersionAsync(
                version,
                message,
                CancellationToken.None,
                allowDisposed: true).ConfigureAwait(false);
        }
    }

    private async Task SaveAfterDelayAsync(
        long version,
        string message,
        CancellationTokenSource cancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_autosaveDelay, cancellationToken).ConfigureAwait(false);
            await PersistVersionAsync(version, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            PersistenceFailed?.Invoke(exception);
            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_scheduledSaveCancellation, cancellation))
                {
                    _scheduledSaveCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task PersistVersionAsync(
        long version,
        string message,
        CancellationToken cancellationToken,
        bool allowDisposed = false)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (!allowDisposed)
                {
                    ObjectDisposedException.ThrowIf(_isDisposed, this);
                }

                if (version != _version)
                {
                    return;
                }
            }

            await PersistAsync(message, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (version == _version)
                {
                    _isDirty = false;
                    _scheduledSaveCancellation = null;
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task PersistAsync(string message, CancellationToken cancellationToken)
    {
        var contents = Encode(message);
        var previousContents = await RepositoryStateFileSystem.ReadIfExistsAsync(
            _messagePath,
            MaximumCommitDraftBytes,
            cancellationToken).ConfigureAwait(false);
        if (previousContents is not null && previousContents.AsSpan().SequenceEqual(contents))
        {
            return;
        }

        if (previousContents is not null)
        {
            await RepositoryStateFileSystem.WriteAtomicallyAsync(
                _backupPath,
                previousContents,
                cancellationToken).ConfigureAwait(false);
        }

        await RepositoryStateFileSystem.WriteAtomicallyAsync(
            _messagePath,
            contents,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteRecoveryFilesAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        foreach (var path in new[] { _messagePath, _backupPath })
        {
            try
            {
                _ = await RepositoryStateFileSystem.DeleteIfExistsAsync(
                    path,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
        {
            throw new IOException("One or more recoverable commit-message files could not be removed.", failure);
        }
    }

    private static byte[] Encode(string message)
    {
        if (message.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException("A recoverable commit-message draft cannot contain NUL.");
        }

        var contents = s_strictUtf8.GetBytes(message);
        if (contents.Length > MaximumCommitDraftBytes)
        {
            throw new InvalidDataException(
                $"The recoverable commit-message draft exceeds {MaximumCommitDraftBytes} UTF-8 bytes.");
        }

        return contents;
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
    }
}
