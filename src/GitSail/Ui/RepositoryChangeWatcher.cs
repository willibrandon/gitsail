using GitSail.Domain;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace GitSail.Ui;

/// <summary>
/// Coalesces repository filesystem notifications and periodic validation into complete Git refreshes.
/// </summary>
internal sealed class RepositoryChangeWatcher : IAsyncDisposable
{
    private static readonly TimeSpan s_maximumNotificationDelay = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly Func<bool, CancellationToken, Task<bool>> _refreshAsync;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _validationInterval;
    private readonly Channel<byte> _signals;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Task _runTask;
    private int _isDisposed;

    /// <summary>
    /// Starts watching the worktree and Git-owned directories represented by one discovered repository.
    /// </summary>
    /// <param name="repository">The exact repository paths discovered through Git.</param>
    /// <param name="operationSupervisor">The repository-session owner for the watcher loop.</param>
    /// <param name="refreshAsync">Runs one complete Git refresh from a notification or validation tick and reports whether it obtained the session gate.</param>
    /// <param name="debounceDelay">The quiet period used to combine an external application's save events.</param>
    /// <param name="validationInterval">The low-frequency full refresh interval that covers missed notifications.</param>
    internal RepositoryChangeWatcher(
        RepositoryLocation repository,
        OperationSupervisor operationSupervisor,
        Func<bool, CancellationToken, Task<bool>> refreshAsync,
        TimeSpan debounceDelay,
        TimeSpan validationInterval)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(operationSupervisor);
        ArgumentNullException.ThrowIfNull(refreshAsync);
        ArgumentOutOfRangeException.ThrowIfLessThan(debounceDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validationInterval, TimeSpan.Zero);
        _refreshAsync = refreshAsync;
        _debounceDelay = debounceDelay;
        _validationInterval = validationInterval;
        _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var paths = new HashSet<string>(pathComparer);
        CollectPath(repository.WorkTree);
        CollectPath(repository.GitDirectory);
        CollectPath(repository.CommonDirectory);
        foreach (var path in paths.Where(candidate => !paths.Any(other =>
            !pathComparer.Equals(candidate, other) && IsDirectoryWithin(candidate, other))))
        {
            TryAddWatcher(path);
        }

        _runTask = operationSupervisor.RunAsync(
            "repository-change-watcher",
            context => RunAsync(context.CancellationToken),
            _cancellation.Token);

        void CollectPath(GitPath? path)
        {
            var systemPath = TryGetSystemPath(path);
            if (systemPath is null)
            {
                return;
            }

            paths.Add(systemPath);
        }
    }

    /// <summary>
    /// Stops filesystem observation and waits for the current refresh callback to leave its safe boundary.
    /// </summary>
    /// <returns>A value task that completes after the background loop has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        await _cancellation.CancelAsync().ConfigureAwait(false);
        _signals.Writer.TryComplete();
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }

        _cancellation.Dispose();
    }

    private void TryAddWatcher(string path)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 16 * 1024,
                NotifyFilter = NotifyFilters.FileName |
                    NotifyFilters.DirectoryName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size |
                    NotifyFilters.CreationTime,
            };
            watcher.Changed += HandleChange;
            watcher.Created += HandleChange;
            watcher.Deleted += HandleChange;
            watcher.Renamed += HandleRename;
            watcher.Error += HandleError;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            UnauthorizedAccessException or
            PlatformNotSupportedException)
        {
            watcher?.Dispose();
            Signal();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var receivedNotification = await WaitForNotificationAsync(cancellationToken).ConfigureAwait(false);
            if (receivedNotification)
            {
                await DebounceNotificationsAsync(cancellationToken).ConfigureAwait(false);
            }

            bool refreshed;
            try
            {
                refreshed = await _refreshAsync(receivedNotification, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                refreshed = true;
            }

            if (!refreshed)
            {
                await Task.Delay(_debounceDelay, cancellationToken).ConfigureAwait(false);
                Signal();
            }
        }
    }

    private async Task DebounceNotificationsAsync(CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            await Task.Delay(_debounceDelay, cancellationToken).ConfigureAwait(false);
            var receivedAnotherNotification = false;
            while (_signals.Reader.TryRead(out _))
            {
                receivedAnotherNotification = true;
            }

            if (!receivedAnotherNotification ||
                Stopwatch.GetElapsedTime(startedAt) >= s_maximumNotificationDelay)
            {
                return;
            }
        }
    }

    private async Task<bool> WaitForNotificationAsync(CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var notification = _signals.Reader.WaitToReadAsync(waitCancellation.Token).AsTask();
        var validation = Task.Delay(_validationInterval, waitCancellation.Token);
        var completed = await Task.WhenAny(notification, validation).ConfigureAwait(false);
        var hasNotification = ReferenceEquals(completed, notification) &&
            await notification.ConfigureAwait(false);
        await waitCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await (ReferenceEquals(completed, notification) ? validation : notification).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        return hasNotification;
    }

    private void HandleChange(object sender, FileSystemEventArgs eventArgs)
        => Signal();

    private void HandleRename(object sender, RenamedEventArgs eventArgs)
        => Signal();

    private void HandleError(object sender, ErrorEventArgs eventArgs)
        => Signal();

    private void Signal()
        => _signals.Writer.TryWrite(0);

    private static string? TryGetSystemPath(GitPath? path)
    {
        if (path is null)
        {
            return null;
        }

        if (path.Kind == NativePathKind.WindowsUtf16)
        {
            return OperatingSystem.IsWindows() ? path.GetWindowsPath() : null;
        }

        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return s_strictUtf8.GetString(path.GetUnixBytes());
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsDirectoryWithin(string candidate, string possibleParent)
    {
        var parent = Path.TrimEndingDirectorySeparator(possibleParent);
        if (parent.Length == 0)
        {
            parent = Path.DirectorySeparatorChar.ToString();
        }

        if (!parent.EndsWith(Path.DirectorySeparatorChar))
        {
            parent += Path.DirectorySeparatorChar;
        }

        return candidate.StartsWith(
            parent,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}
