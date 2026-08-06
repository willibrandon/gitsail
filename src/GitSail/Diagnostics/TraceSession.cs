using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Ui;
using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;

namespace GitSail.Diagnostics;

/// <summary>
/// Owns one bounded JSON Lines trace file and its sanitized in-memory presentation.
/// </summary>
internal sealed class TraceSession : IDisposable
{
    private const int MaximumDisplayEntries = 2_000;
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private const int MaximumRetainedAutomaticFiles = 10;
    private static readonly byte[] s_newLine = [(byte)'\n'];
    private readonly Lock _lock = new();
    private readonly FileStream _stream;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<TraceDisplayEntry> _displayEntries = new();
    private long _eventSequence;
    private long _childSequence;
    private GitSailLogLevel _minimumLevel = GitSailLogLevel.Information;
    private bool _fileLimitReached;
    private bool _disposed;

    private TraceSession(
        string filePath,
        bool generatedPath,
        FileStream stream,
        TimeProvider timeProvider)
    {
        FilePath = filePath;
        GeneratedPath = generatedPath;
        _stream = stream;
        _timeProvider = timeProvider;
        WriteEvent(
            GitSailLogLevel.Information,
            "trace.started",
            "Trace capture started.",
            writer => writer.WriteBoolean("generatedPath", generatedPath));
    }

    /// <summary>
    /// Gets the fully qualified trace output path.
    /// </summary>
    internal string FilePath { get; }

    /// <summary>
    /// Gets whether GitSail selected the trace output path.
    /// </summary>
    internal bool GeneratedPath { get; }

    /// <summary>
    /// Creates one bounded trace at an explicit path or a generated private user-state path.
    /// </summary>
    /// <param name="options">The typed trace request.</param>
    /// <param name="environment">The classified process environment used for user directories.</param>
    /// <param name="timeProvider">The UTC event clock.</param>
    /// <returns>The opened trace session.</returns>
    internal static TraceSession Create(
        TraceOptions options,
        IProcessEnvironment environment,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (options.OutputFile is { } requestedPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
            if (requestedPath.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("A trace path cannot contain NUL.", nameof(options));
            }

            var filePath = Path.GetFullPath(requestedPath, Environment.CurrentDirectory);
            return new TraceSession(
                filePath,
                generatedPath: false,
                OpenNewFile(filePath),
                timeProvider);
        }

        var traceDirectory = Path.Combine(
            new UserDirectoryPathService(environment).GetStateDirectory(),
            "traces");
        UserDirectoryFileSystem.EnsurePrivateDirectory(traceDirectory);
        PruneAutomaticFiles(traceDirectory);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'");
            var filePath = Path.Combine(
                traceDirectory,
                $"trace-{timestamp}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}.jsonl");
            try
            {
                return new TraceSession(
                    filePath,
                    generatedPath: true,
                    OpenNewFile(filePath),
                    timeProvider);
            }
            catch (IOException) when (File.Exists(filePath))
            {
            }
        }

        throw new IOException("GitSail could not allocate a unique trace file.");
    }

    /// <summary>
    /// Records the selected top-level application mode.
    /// </summary>
    /// <param name="mode">The System.CommandLine-selected mode.</param>
    internal void WriteApplicationStarted(ApplicationMode mode)
        => WriteEvent(
            GitSailLogLevel.Information,
            "application.started",
            $"Started {mode.ToString().ToLowerInvariant()} mode.",
            writer => writer.WriteString("mode", mode.ToString().ToLowerInvariant()));

    /// <summary>
    /// Records the final process exit code after the terminal has been restored.
    /// </summary>
    /// <param name="exitCode">The documented application exit code.</param>
    internal void WriteApplicationCompleted(int exitCode)
        => WriteEvent(
            GitSailLogLevel.Information,
            "application.completed",
            $"Application completed with exit code {exitCode}.",
            writer => writer.WriteNumber("exitCode", exitCode));

    /// <summary>
    /// Records an application-boundary failure without retaining its message or stack trace.
    /// </summary>
    /// <param name="exception">The application-boundary exception.</param>
    internal void WriteApplicationFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        WriteEvent(
            GitSailLogLevel.Error,
            "application.failed",
            $"Application failed with {exception.GetType().Name}.",
            writer =>
            {
                writer.WriteString("exceptionType", exception.GetType().FullName);
                writer.WriteBoolean("cancelled", exception is OperationCanceledException);
            });
    }

    /// <summary>
    /// Records a secret-free child start and returns its trace-local identifier.
    /// </summary>
    /// <param name="invocation">The complete typed child invocation.</param>
    /// <param name="terminalAttached">Whether the child inherits terminal streams.</param>
    /// <returns>The positive trace-local child operation identifier.</returns>
    internal long WriteChildStarted(ProcessInvocation invocation, bool terminalAttached)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var operationId = Interlocked.Increment(ref _childSequence);
        WriteEvent(
            GitSailLogLevel.Debug,
            "child.started",
            $"Started {invocation.Executable.Kind} child {operationId}.",
            writer =>
            {
                writer.WriteNumber("operationId", operationId);
                writer.WriteString("program", invocation.Executable.Kind.ToString());
                writer.WriteNumber("argumentCount", invocation.Arguments.Length);
                writer.WriteNumber("standardInputBytes", invocation.StandardInput.GetBytes().Length);
                writer.WriteBoolean("terminalAttached", terminalAttached);
            });
        return operationId;
    }

    /// <summary>
    /// Records a redirected child result without retaining stream content.
    /// </summary>
    /// <param name="operationId">The trace-local child operation identifier.</param>
    /// <param name="result">The bounded child result.</param>
    internal void WriteChildCompleted(long operationId, ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        WriteEvent(
            result.ExitCode == 0 ? GitSailLogLevel.Debug : GitSailLogLevel.Warning,
            "child.completed",
            $"Child {operationId} exited with code {result.ExitCode}.",
            writer =>
            {
                writer.WriteNumber("operationId", operationId);
                writer.WriteNumber("exitCode", result.ExitCode);
                writer.WriteNumber("durationMilliseconds", result.Duration.TotalMilliseconds);
                writer.WriteNumber(
                    "standardOutputBytes",
                    result.StandardOutputSpool?.Length ?? result.StandardOutput.Length);
                writer.WriteNumber("standardErrorBytes", result.StandardError.Length);
            });
    }

    /// <summary>
    /// Records a terminal-attached child result without terminal stream content.
    /// </summary>
    /// <param name="operationId">The trace-local child operation identifier.</param>
    /// <param name="exitCode">The normalized child exit status.</param>
    /// <param name="duration">The elapsed child duration.</param>
    internal void WriteTerminalChildCompleted(long operationId, int exitCode, TimeSpan duration)
        => WriteEvent(
            exitCode == 0 ? GitSailLogLevel.Debug : GitSailLogLevel.Warning,
            "child.completed",
            $"Child {operationId} exited with code {exitCode}.",
            writer =>
            {
                writer.WriteNumber("operationId", operationId);
                writer.WriteNumber("exitCode", exitCode);
                writer.WriteNumber("durationMilliseconds", duration.TotalMilliseconds);
                writer.WriteBoolean("terminalAttached", true);
            });

    /// <summary>
    /// Records a child failure without exception messages, arguments, environment, input, or output.
    /// </summary>
    /// <param name="operationId">The trace-local child operation identifier.</param>
    /// <param name="exception">The child-boundary exception.</param>
    /// <param name="duration">The elapsed time before failure.</param>
    internal void WriteChildFailed(long operationId, Exception exception, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(exception);
        WriteEvent(
            GitSailLogLevel.Error,
            "child.failed",
            $"Child {operationId} failed with {exception.GetType().Name}.",
            writer =>
            {
                writer.WriteNumber("operationId", operationId);
                writer.WriteString("exceptionType", exception.GetType().FullName);
                writer.WriteBoolean("cancelled", exception is OperationCanceledException);
                writer.WriteNumber("durationMilliseconds", duration.TotalMilliseconds);
            });
    }

    /// <summary>
    /// Gets an immutable snapshot of sanitized display entries.
    /// </summary>
    /// <returns>The retained display entries in event order.</returns>
    internal ImmutableArray<TraceDisplayEntry> GetDisplayEntries()
    {
        lock (_lock)
        {
            return [.. _displayEntries];
        }
    }

    /// <summary>
    /// Changes the minimum severity retained by subsequent trace events.
    /// </summary>
    /// <param name="minimumLevel">The new configured minimum severity.</param>
    internal void SetMinimumLevel(GitSailLogLevel minimumLevel)
    {
        if (!Enum.IsDefined(minimumLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLevel));
        }

        lock (_lock)
        {
            _minimumLevel = minimumLevel;
        }
    }

    /// <summary>
    /// Flushes and closes the trace file.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
            _disposed = true;
        }
    }

    private void WriteEvent(
        GitSailLogLevel level,
        string eventName,
        string message,
        Action<Utf8JsonWriter> writeDetails)
    {
        lock (_lock)
        {
            if (_disposed || _minimumLevel == GitSailLogLevel.None || level < _minimumLevel)
            {
                return;
            }

            var timestamp = _timeProvider.GetUtcNow();
            var displayEntry = new TraceDisplayEntry(
                timestamp,
                eventName,
                TerminalTextSanitizer.Sanitize(message));
            RetainDisplayEntry(displayEntry);
            if (_fileLimitReached)
            {
                return;
            }

            var buffer = new ArrayBufferWriter<byte>(512);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteNumber("sequence", ++_eventSequence);
                writer.WriteString("timestampUtc", timestamp);
                writer.WriteString("event", eventName);
                writer.WriteString("message", displayEntry.Message);
                writeDetails(writer);
                writer.WriteEndObject();
            }

            if (_stream.Position + buffer.WrittenCount + 1 > MaximumFileBytes)
            {
                _fileLimitReached = true;
                RetainDisplayEntry(new TraceDisplayEntry(
                    timestamp,
                    "trace.limit",
                    $"Trace stopped at the {MaximumFileBytes} byte limit."));
                return;
            }

            _stream.Write(buffer.WrittenSpan);
            _stream.Write(s_newLine);
            _stream.Flush();
        }
    }

    private void RetainDisplayEntry(TraceDisplayEntry entry)
    {
        _displayEntries.Enqueue(entry);
        while (_displayEntries.Count > MaximumDisplayEntries)
        {
            _ = _displayEntries.Dequeue();
        }
    }

    private static FileStream OpenNewFile(string filePath)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            BufferSize = 16 * 1024,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(filePath, options);
    }

    private static void PruneAutomaticFiles(string traceDirectory)
    {
        try
        {
            var files = new DirectoryInfo(traceDirectory)
                .EnumerateFiles("trace-*.jsonl", SearchOption.TopDirectoryOnly)
                .Where(static file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(static file => file.CreationTimeUtc)
                .ThenByDescending(static file => file.Name, StringComparer.Ordinal)
                .Skip(MaximumRetainedAutomaticFiles - 1)
                .ToArray();
            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
