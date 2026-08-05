using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Captures exact stash state and performs revalidated Git-owned stash transactions.
/// </summary>
internal sealed class StashService
{
    private const int MaximumStableCaptureAttempts = 3;
    private const int MaximumCatalogBytes = 512 * 1024 * 1024;
    private const int MaximumOperationOutputBytes = 64 * 1024 * 1024;
    private const int MaximumDiffBytes = 1024 * 1024 * 1024;
    private const int SpoolMemoryThresholdBytes = 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly RepositoryPreconditionService _preconditionService;
    private readonly RepositoryWorktreeFingerprintService _worktreeFingerprintService;
    private readonly StashCatalogParser _parser;

    /// <summary>
    /// Initializes stash capture and mutation over one repository mutation coordinator.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    internal StashService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        RepositoryMutationCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(coordinator);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _coordinator = coordinator;
        _preconditionService = new RepositoryPreconditionService(installation, runner, environmentFactory);
        _worktreeFingerprintService = new RepositoryWorktreeFingerprintService(
            installation,
            runner,
            environmentFactory);
        _parser = new StashCatalogParser();
    }

    /// <summary>
    /// Captures one stable exact stash reflog and complete worktree catalog.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="cancellationToken">Signals catalog capture cancellation.</param>
    /// <returns>The stable stash catalog and repository identities.</returns>
    internal async Task<StashCatalog> CaptureAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        for (var attempt = 0; attempt < MaximumStableCaptureAttempts; attempt++)
        {
            var before = await _preconditionService.CaptureOnceAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var firstReflog = await CaptureReflogOnceAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            if (!firstReflog.Stable)
            {
                continue;
            }

            var worktreeFingerprint = await _worktreeFingerprintService.CaptureAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var secondReflog = await CaptureReflogOnceAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var after = await _preconditionService.CaptureOnceAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            if (before.Matches(after) &&
                secondReflog.Stable &&
                Equals(firstReflog.Tip, secondReflog.Tip) &&
                EntriesMatch(firstReflog.Entries, secondReflog.Entries))
            {
                return new StashCatalog(after, worktreeFingerprint, secondReflog.Entries);
            }
        }

        throw new RepositoryPreconditionException(
            "The stash reflog or repository continued changing while GitSail prepared the stash view; retry the refresh.");
    }

    /// <summary>
    /// Creates a stash from the exact displayed HEAD and index generation using typed options.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="expectedPrecondition">The exact displayed HEAD and index identity.</param>
    /// <param name="options">The validated noninteractive stash-create options.</param>
    /// <param name="cancellationToken">Signals stash creation cancellation.</param>
    /// <returns>The successful Git operation output and warnings.</returns>
    internal async Task<GitOperationResult> CreateAsync(
        CanonicalDirectory workingDirectory,
        RepositoryPrecondition expectedPrecondition,
        StashCreateOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedPrecondition);
        ArgumentNullException.ThrowIfNull(options);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Stash,
            cancellationToken).ConfigureAwait(false);
        var livePrecondition = await _preconditionService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!expectedPrecondition.Matches(livePrecondition))
        {
            throw new RepositoryPreconditionException(
                "HEAD, its branch attachment, or the index changed after the stash action was prepared; refresh and retry.");
        }

        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("stash"),
            ProcessArgument.Literal("push"),
        };
        if (options.StagedOnly)
        {
            arguments.Add(ProcessArgument.Literal("--staged"));
        }
        else if (options.KeepIndex)
        {
            arguments.Add(ProcessArgument.Literal("--keep-index"));
        }

        arguments.Add(options.FileScope switch
        {
            StashFileScope.Tracked => ProcessArgument.Literal("--no-include-untracked"),
            StashFileScope.IncludeUntracked => ProcessArgument.Literal("--include-untracked"),
            StashFileScope.IncludeIgnored => ProcessArgument.Literal("--all"),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        });
        if (options.Message.Length > 0)
        {
            arguments.Add(ProcessArgument.Literal("--message"));
            arguments.Add(ProcessArgument.Literal(options.Message));
        }

        arguments.Add(ProcessArgument.Literal("--"));
        return await RunMutationAsync(
            workingDirectory,
            arguments,
            _environmentFactory.CreateCommitEnvironment(),
            "Git could not create the stash.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures the complete exact patch for one still-current displayed stash entry.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="expectedCatalog">The exact displayed stash catalog.</param>
    /// <param name="stash">The exact displayed stash entry.</param>
    /// <param name="cancellationToken">Signals patch capture cancellation.</param>
    /// <returns>An owned exact-byte spool that the caller must dispose.</returns>
    internal async Task<RawByteSpool> ShowAsync(
        CanonicalDirectory workingDirectory,
        StashCatalog expectedCatalog,
        StashInfo stash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ArgumentNullException.ThrowIfNull(stash);
        var liveEntries = await CaptureReflogAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        RequireMatchingEntry(expectedCatalog, liveEntries, stash);
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--literal-pathspecs"),
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("stash"),
                ProcessArgument.Literal("show"),
                ProcessArgument.Literal("--patch"),
                ProcessArgument.Literal("--binary"),
                ProcessArgument.Literal("--full-index"),
                ProcessArgument.Literal("--no-color"),
                ProcessArgument.Literal("--no-ext-diff"),
                ProcessArgument.Literal("--no-textconv"),
                ProcessArgument.Literal("--include-untracked"),
                ProcessArgument.Literal(stash.ObjectId.ToString()),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.CreateSpooling(
                SpoolMemoryThresholdBytes,
                MaximumDiffBytes,
                MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        var spool = result.StandardOutputSpool
            ?? throw new InvalidOperationException("Stash patch capture did not return its required byte spool.");
        if (result.ExitCode != 0)
        {
            spool.Dispose();
            throw CreateCommandException(result, "Git could not show the selected stash.");
        }

        return spool;
    }

    /// <summary>
    /// Applies one exact displayed stash commit without removing its reflog entry.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="expectedCatalog">The exact displayed repository and stash catalog.</param>
    /// <param name="stash">The exact displayed stash entry.</param>
    /// <param name="restoreIndex">Whether Git should also restore the stash's index state.</param>
    /// <param name="cancellationToken">Signals stash application cancellation.</param>
    /// <returns>The successful Git operation output and warnings.</returns>
    internal Task<GitOperationResult> ApplyAsync(
        CanonicalDirectory workingDirectory,
        StashCatalog expectedCatalog,
        StashInfo stash,
        bool restoreIndex,
        CancellationToken cancellationToken)
    {
        var arguments = ImmutableArray.CreateBuilder<ProcessArgument>(restoreIndex ? 4 : 3);
        arguments.Add(ProcessArgument.Literal("stash"));
        arguments.Add(ProcessArgument.Literal("apply"));
        if (restoreIndex)
        {
            arguments.Add(ProcessArgument.Literal("--index"));
        }

        arguments.Add(ProcessArgument.Literal(stash.ObjectId.ToString()));
        return RunCatalogMutationAsync(
            workingDirectory,
            expectedCatalog,
            stash,
            arguments.MoveToImmutable(),
            requireWorktreeMatch: true,
            "Git could not apply the selected stash.",
            cancellationToken);
    }

    /// <summary>
    /// Pops one exact displayed stash selector after complete worktree revalidation.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="expectedCatalog">The exact displayed repository and stash catalog.</param>
    /// <param name="stash">The exact displayed stash entry.</param>
    /// <param name="restoreIndex">Whether Git should also restore the stash's index state.</param>
    /// <param name="cancellationToken">Signals stash pop cancellation.</param>
    /// <returns>The successful Git operation output and warnings.</returns>
    internal Task<GitOperationResult> PopAsync(
        CanonicalDirectory workingDirectory,
        StashCatalog expectedCatalog,
        StashInfo stash,
        bool restoreIndex,
        CancellationToken cancellationToken)
    {
        var arguments = ImmutableArray.CreateBuilder<ProcessArgument>(restoreIndex ? 4 : 3);
        arguments.Add(ProcessArgument.Literal("stash"));
        arguments.Add(ProcessArgument.Literal("pop"));
        if (restoreIndex)
        {
            arguments.Add(ProcessArgument.Literal("--index"));
        }

        arguments.Add(ProcessArgument.Literal(stash.Selector));
        return RunCatalogMutationAsync(
            workingDirectory,
            expectedCatalog,
            stash,
            arguments.MoveToImmutable(),
            requireWorktreeMatch: true,
            "Git could not pop the selected stash; Git retains it when application does not complete cleanly.",
            cancellationToken);
    }

    /// <summary>
    /// Drops one exact displayed stash selector after complete reflog revalidation.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="expectedCatalog">The exact displayed stash catalog.</param>
    /// <param name="stash">The exact displayed stash entry.</param>
    /// <param name="cancellationToken">Signals stash deletion cancellation.</param>
    /// <returns>The successful Git operation output and warnings.</returns>
    internal Task<GitOperationResult> DropAsync(
        CanonicalDirectory workingDirectory,
        StashCatalog expectedCatalog,
        StashInfo stash,
        CancellationToken cancellationToken)
        => RunCatalogMutationAsync(
            workingDirectory,
            expectedCatalog,
            stash,
            [
                ProcessArgument.Literal("stash"),
                ProcessArgument.Literal("drop"),
                ProcessArgument.Literal(stash.Selector),
            ],
            requireWorktreeMatch: false,
            "Git could not drop the selected stash.",
            cancellationToken);

    private async Task<GitOperationResult> RunCatalogMutationAsync(
        CanonicalDirectory workingDirectory,
        StashCatalog expectedCatalog,
        StashInfo stash,
        ImmutableArray<ProcessArgument> arguments,
        bool requireWorktreeMatch,
        string fallbackError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ArgumentNullException.ThrowIfNull(stash);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.Stash,
            cancellationToken).ConfigureAwait(false);
        if (requireWorktreeMatch)
        {
            var liveCatalog = await CaptureAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            RequireMatchingEntry(expectedCatalog, liveCatalog, stash);
        }
        else
        {
            var liveEntries = await CaptureReflogAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            RequireMatchingEntry(expectedCatalog, liveEntries, stash);
        }

        return await RunMutationAsync(
            workingDirectory,
            arguments,
            _environmentFactory.CreateCheckoutEnvironment(),
            fallbackError,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitOperationResult> RunMutationAsync(
        CanonicalDirectory workingDirectory,
        IReadOnlyList<ProcessArgument> arguments,
        ChildEnvironment environment,
        string fallbackError,
        CancellationToken cancellationToken)
    {
        var completeArguments = new List<ProcessArgument>(arguments.Count + 1)
        {
            ProcessArgument.Literal("--no-pager"),
        };
        completeArguments.AddRange(arguments);
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. completeArguments],
            workingDirectory,
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumOperationOutputBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, fallbackError);
        }

        return new GitOperationResult(result.StandardOutput, result.StandardError);
    }

    private async Task<ObjectId?> ReadStashRefAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("rev-parse"),
                ProcessArgument.Literal("--verify"),
                ProcessArgument.Literal("--quiet"),
                ProcessArgument.Literal("refs/stash"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1 && result.StandardOutput.IsEmpty)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not resolve refs/stash.");
        }

        var field = TrimLineEnding(result.StandardOutput.Span);
        return ObjectId.TryParseHex(field, out var objectId)
            ? objectId
            : throw new InvalidDataException("Git returned an invalid refs/stash object identifier.");
    }

    private async Task<ImmutableArray<StashInfo>> CaptureReflogAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumStableCaptureAttempts; attempt++)
        {
            var first = await CaptureReflogOnceAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var second = await CaptureReflogOnceAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            if (first.Stable &&
                second.Stable &&
                Equals(first.Tip, second.Tip) &&
                EntriesMatch(first.Entries, second.Entries))
            {
                return second.Entries;
            }
        }

        throw new RepositoryPreconditionException(
            "The stash reflog continued changing while GitSail prepared the action; refresh and retry.");
    }

    private async Task<(bool Stable, ObjectId? Tip, ImmutableArray<StashInfo> Entries)> CaptureReflogOnceAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var beforeRef = await ReadStashRefAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var read = beforeRef is null
            ? (Succeeded: true, Output: ReadOnlyMemory<byte>.Empty)
            : await TryReadCatalogOutputAsync(
                workingDirectory,
                beforeRef,
                cancellationToken).ConfigureAwait(false);
        if (!read.Succeeded)
        {
            return (false, null, ImmutableArray<StashInfo>.Empty);
        }

        var afterRef = await ReadStashRefAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (!Equals(beforeRef, afterRef))
        {
            return (false, null, ImmutableArray<StashInfo>.Empty);
        }

        var entries = _parser.Parse(read.Output.Span);
        if ((entries.IsEmpty && afterRef is not null) ||
            (!entries.IsEmpty && !entries[0].ObjectId.Equals(afterRef)))
        {
            throw new InvalidDataException(
                "Git's stash ref and first reflog entry reported different object identifiers.");
        }

        return (true, afterRef, entries);
    }

    private async Task<(bool Succeeded, ReadOnlyMemory<byte> Output)> TryReadCatalogOutputAsync(
        CanonicalDirectory workingDirectory,
        ObjectId expectedRef,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("log"),
                ProcessArgument.Literal("-g"),
                ProcessArgument.Literal("-z"),
                ProcessArgument.Literal("--format=%H%x00%gD%x00%gs%x00%ct%x00"),
                ProcessArgument.Literal("refs/stash"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumCatalogBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var currentRef = await ReadStashRefAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            if (!expectedRef.Equals(currentRef))
            {
                return (false, ReadOnlyMemory<byte>.Empty);
            }

            throw CreateCommandException(result, "Git could not enumerate the stash reflog.");
        }

        return (true, result.StandardOutput);
    }

    private static void RequireMatchingEntry(
        StashCatalog expectedCatalog,
        StashCatalog liveCatalog,
        StashInfo expectedEntry)
    {
        if (!expectedCatalog.Matches(liveCatalog) || liveCatalog.FindMatching(expectedEntry) is null)
        {
            throw new RepositoryPreconditionException(
                "The selected stash, HEAD, index, or worktree changed after the action was prepared; refresh and retry.");
        }
    }

    private static void RequireMatchingEntry(
        StashCatalog expectedCatalog,
        ImmutableArray<StashInfo> liveEntries,
        StashInfo expectedEntry)
    {
        if (!EntriesMatch(expectedCatalog.Entries, liveEntries) ||
            expectedEntry.Index >= liveEntries.Length ||
            !liveEntries[expectedEntry.Index].Matches(expectedEntry))
        {
            throw new RepositoryPreconditionException(
                "The stash reflog changed after the action was prepared; refresh and retry.");
        }
    }

    private static bool EntriesMatch(
        ImmutableArray<StashInfo> first,
        ImmutableArray<StashInfo> second)
        => first.Length == second.Length &&
            first.Zip(second).All(pair => pair.First.Matches(pair.Second));

    private static GitCommandException CreateCommandException(ProcessResult result, string fallbackError)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        return new GitCommandException(
            result.ExitCode,
            string.IsNullOrEmpty(error) ? fallbackError : error);
    }

    private static ReadOnlySpan<byte> TrimLineEnding(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty && bytes[^1] is (byte)'\r' or (byte)'\n')
        {
            bytes = bytes[..^1];
        }

        return bytes;
    }
}
