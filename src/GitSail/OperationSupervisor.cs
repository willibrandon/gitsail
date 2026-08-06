using GitSail.Domain;
using System.Collections.Immutable;
using System.Threading.Channels;

namespace GitSail;

/// <summary>
/// Owns background operations through cancellation, progress, failure observation, and shutdown joining.
/// </summary>
internal sealed class OperationSupervisor : IAsyncDisposable
{
    private const int UpdateCapacity = 64;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly Dictionary<OperationId, CancellationTokenSource> _operationCancellations = [];
    private readonly Dictionary<OperationId, Task> _operationTasks = [];
    private readonly Dictionary<OperationId, OperationSnapshot> _activeOperations = [];
    private readonly Channel<OperationSnapshot> _updates;
    private long _nextOperationId;
    private long _nextSequence;
    private int _shutdownStarted;

    /// <summary>
    /// Initializes an empty supervisor using the supplied clock for immutable snapshots.
    /// </summary>
    /// <param name="timeProvider">The clock used to timestamp operation snapshots.</param>
    internal OperationSupervisor(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _updates = Channel.CreateBounded<OperationSnapshot>(new BoundedChannelOptions(UpdateCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// Gets the bounded stream of immutable operation lifecycle updates.
    /// </summary>
    internal ChannelReader<OperationSnapshot> Updates => _updates.Reader;

    /// <summary>
    /// Gets an immutable snapshot of all operations that have not reached a terminal state.
    /// </summary>
    internal ImmutableArray<OperationSnapshot> ActiveOperations
    {
        get
        {
            lock (_gate)
            {
                return [.. _activeOperations.Values.OrderBy(static snapshot => snapshot.Id)];
            }
        }
    }

    /// <summary>
    /// Starts one owned background operation whose failure is observed through the update stream.
    /// </summary>
    /// <param name="name">The stable nonempty operation name.</param>
    /// <param name="operation">The complete cancellable operation.</param>
    /// <param name="cancellationToken">Adds caller cancellation to supervisor shutdown.</param>
    /// <returns>The stable identifier of the accepted operation.</returns>
    internal OperationId Start(
        string name,
        Func<OperationContext, Task> operation,
        CancellationToken cancellationToken = default)
        => StartCore(name, operation, propagateFailure: false, cancellationToken).Id;

    /// <summary>
    /// Runs one owned operation and propagates its observed failure to the awaiting caller.
    /// </summary>
    /// <param name="name">The stable nonempty operation name.</param>
    /// <param name="operation">The complete cancellable operation.</param>
    /// <param name="cancellationToken">Adds caller cancellation to supervisor shutdown.</param>
    /// <returns>A task that completes after the operation reaches its terminal state.</returns>
    internal Task RunAsync(
        string name,
        Func<OperationContext, Task> operation,
        CancellationToken cancellationToken = default)
        => StartCore(name, operation, propagateFailure: true, cancellationToken).Task;

    /// <summary>
    /// Requests cancellation of one active operation without affecting its siblings.
    /// </summary>
    /// <param name="id">The stable identifier returned when the operation started.</param>
    /// <returns><see langword="true"/> when the operation was active.</returns>
    internal bool Cancel(OperationId id)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _operationCancellations.TryGetValue(id, out cancellation);
        }

        if (cancellation is null)
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Requests cancellation of every active operation while keeping the supervisor reusable.
    /// </summary>
    internal void CancelAll()
    {
        CancellationTokenSource[] cancellations;
        lock (_gate)
        {
            cancellations = [.. _operationCancellations.Values];
        }

        CancelAll(cancellations);
    }

    /// <summary>
    /// Waits until every operation active at the join boundary has reached a terminal state.
    /// </summary>
    /// <param name="cancellationToken">Bounds the join without canceling owned operations.</param>
    /// <returns>A task that completes after the captured operations have stopped.</returns>
    internal Task JoinAsync(CancellationToken cancellationToken = default)
    {
        Task[] tasks;
        lock (_gate)
        {
            tasks = [.. _operationTasks.Values];
        }

        return ObserveAllAsync(tasks).WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Stops accepting work, cancels every operation, and joins all owned tasks.
    /// </summary>
    /// <param name="cancellationToken">Bounds the caller's wait for graceful shutdown.</param>
    /// <returns>A task that completes after every captured operation has stopped.</returns>
    internal async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var beginShutdown = Interlocked.Exchange(ref _shutdownStarted, 1) == 0;
        if (beginShutdown)
        {
            _shutdownCancellation.Cancel();
        }

        Task[] tasks;
        CancellationTokenSource[] cancellations;
        lock (_gate)
        {
            cancellations = [.. _operationCancellations.Values];
            tasks = [.. _operationTasks.Values];
        }

        CancelAll(cancellations);
        await ObserveAllAsync(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        _updates.Writer.TryComplete();
    }

    /// <summary>
    /// Cancels and joins every owned operation before releasing supervisor resources.
    /// </summary>
    /// <returns>A value task that completes after application-exit joining.</returns>
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _shutdownCancellation.Dispose();
    }

    private (OperationId Id, Task Task) StartCore(
        string name,
        Func<OperationContext, Task> operation,
        bool propagateFailure,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _shutdownStarted) != 0, this);
            var id = new OperationId(checked(++_nextOperationId));
            var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _shutdownCancellation.Token,
                cancellationToken);
            _operationCancellations.Add(id, operationCancellation);
            PublishLocked(id, name, OperationState.Started, detail: null, progress: null, failure: null);
            var context = new OperationContext(
                id,
                operationCancellation.Token,
                (detail, progress) => Report(id, name, detail, progress));
            var task = ExecuteAsync(name, operation, context, operationCancellation, propagateFailure);
            _operationTasks.Add(id, task);
            return (id, task);
        }
    }

    private async Task ExecuteAsync(
        string name,
        Func<OperationContext, Task> operation,
        OperationContext context,
        CancellationTokenSource cancellation,
        bool propagateFailure)
    {
        await Task.Yield();
        try
        {
            await operation(context).ConfigureAwait(false);
            Publish(context.Id, name, OperationState.Completed, detail: null, progress: 1, failure: null);
        }
        catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
        {
            Publish(context.Id, name, OperationState.Canceled, detail: null, progress: null, exception);
            if (propagateFailure)
            {
                throw;
            }
        }
        catch (Exception exception)
        {
            Publish(context.Id, name, OperationState.Failed, detail: null, progress: null, exception);
            if (propagateFailure)
            {
                throw;
            }
        }
        finally
        {
            lock (_gate)
            {
                _operationTasks.Remove(context.Id);
                _operationCancellations.Remove(context.Id);
                _activeOperations.Remove(context.Id);
            }

            cancellation.Dispose();
        }
    }

    private void Report(OperationId id, string name, string? detail, double? progress)
        => Publish(id, name, OperationState.Running, detail, progress, failure: null);

    private void Publish(
        OperationId id,
        string name,
        OperationState state,
        string? detail,
        double? progress,
        Exception? failure)
    {
        lock (_gate)
        {
            if (_activeOperations.ContainsKey(id))
            {
                PublishLocked(id, name, state, detail, progress, failure);
            }
        }
    }

    private void PublishLocked(
        OperationId id,
        string name,
        OperationState state,
        string? detail,
        double? progress,
        Exception? failure)
    {
        var snapshot = new OperationSnapshot(
            checked(++_nextSequence),
            id,
            name,
            state,
            detail,
            progress,
            _timeProvider.GetUtcNow(),
            failure);
        _activeOperations[id] = snapshot;
        _updates.Writer.TryWrite(snapshot);
    }

    private static void CancelAll(IEnumerable<CancellationTokenSource> cancellations)
    {
        foreach (var cancellation in cancellations)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async Task ObserveAllAsync(IEnumerable<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
