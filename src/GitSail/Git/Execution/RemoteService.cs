using GitSail.Domain;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Captures stable remote configuration and performs revalidated Git-owned remote transactions.
/// </summary>
internal sealed class RemoteService
{
    private const int MaximumRemoteCount = 100_000;
    private const int MaximumRemoteNameBytes = 1024 * 1024;
    private const int MaximumConfigurationBytes = 64 * 1024 * 1024;
    private const int MaximumTransportOutputBytes = 256 * 1024 * 1024;
    private const int MaximumErrorBytes = 64 * 1024 * 1024;
    private const int MaximumStableCaptureAttempts = 3;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly CredentialPromptBroker _credentialPromptBroker;

    /// <summary>
    /// Initializes remote capture and mutation over one repository mutation coordinator.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    /// <param name="credentialPromptBroker">The operation-scoped authenticated credential broker.</param>
    internal RemoteService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        RepositoryMutationCoordinator coordinator,
        CredentialPromptBroker credentialPromptBroker)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(credentialPromptBroker);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _coordinator = coordinator;
        _credentialPromptBroker = credentialPromptBroker;
    }

    /// <summary>
    /// Captures one byte-stable complete remote-name and URL catalog.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="cancellationToken">Signals catalog capture cancellation.</param>
    /// <returns>The stable complete configured remote catalog.</returns>
    internal async Task<RemoteCatalog> CaptureAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        for (var attempt = 0; attempt < MaximumStableCaptureAttempts; attempt++)
        {
            var first = await ReadCatalogAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            var second = await ReadCatalogAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            if (first.Matches(second))
            {
                return second;
            }
        }

        throw new RepositoryPreconditionException(
            "Remote names or URLs continued changing while GitSail prepared the remote view; retry the refresh.");
    }

    /// <summary>
    /// Validates one user-entered remote name through Git's reference-format rules.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="candidate">The user-entered candidate remote name.</param>
    /// <param name="cancellationToken">Signals validation cancellation.</param>
    /// <returns>The exact validated remote name.</returns>
    internal async Task<RemoteName> ValidateNameAsync(
        CanonicalDirectory workingDirectory,
        string candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        if (candidate.Contains('\0', StringComparison.Ordinal))
        {
            throw new RemoteOperationException("A remote name cannot contain NUL.");
        }

        byte[] candidateBytes;
        try
        {
            candidateBytes = s_strictUtf8.GetBytes(candidate);
        }
        catch (EncoderFallbackException)
        {
            throw new RemoteOperationException("The remote name contains invalid Unicode text.");
        }

        var validationReference = new byte[
            "refs/remotes/"u8.Length + candidateBytes.Length + "/gitsail-validation"u8.Length];
        "refs/remotes/"u8.CopyTo(validationReference);
        candidateBytes.CopyTo(validationReference.AsSpan("refs/remotes/"u8.Length));
        "/gitsail-validation"u8.CopyTo(
            validationReference.AsSpan("refs/remotes/"u8.Length + candidateBytes.Length));
        var result = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("check-ref-format"),
                ProcessArgument.Native(RefName.FromBytes(validationReference)),
            ],
            1024,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git rejected the remote name.");
        }

        return RemoteName.FromBytes(candidateBytes);
    }

    /// <summary>
    /// Adds one exact validated remote after proving the displayed complete catalog is current.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="expectedCatalog">The exact complete remote catalog displayed to the user.</param>
    /// <param name="name">The Git-validated destination remote name.</param>
    /// <param name="url">The exact user-entered remote URL.</param>
    /// <param name="cancellationToken">Signals remote-add cancellation.</param>
    /// <returns>Git's exact successful operation output.</returns>
    internal async Task<GitOperationResult> AddAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteName name,
        RemoteUrl url,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(url);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.RemoteMutation,
            cancellationToken).ConfigureAwait(false);
        var liveCatalog = await CaptureAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        RequireMatchingCatalog(expectedCatalog, liveCatalog);
        if (liveCatalog.Find(name) is not null)
        {
            throw new RepositoryPreconditionException(
                "The destination remote now exists; refresh before choosing another name.");
        }

        return await RunMutationAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("remote"),
                ProcessArgument.Literal("add"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(name),
                ProcessArgument.Native(url),
            ],
            "Git could not add the remote.",
            [url],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes one exact selected remote after proving its complete configuration remains current.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="expectedCatalog">The exact complete remote catalog displayed to the user.</param>
    /// <param name="remote">The exact selected remote to remove.</param>
    /// <param name="cancellationToken">Signals remote-removal cancellation.</param>
    /// <returns>Git's exact successful operation output.</returns>
    internal async Task<GitOperationResult> RemoveAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteInfo remote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.RemoteMutation,
            cancellationToken).ConfigureAwait(false);
        var liveRemote = await RevalidateRemoteAsync(
            workingDirectory,
            expectedCatalog,
            remote,
            cancellationToken).ConfigureAwait(false);
        return await RunMutationAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("remote"),
                ProcessArgument.Literal("remove"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(liveRemote.Name),
            ],
            "Git could not remove the selected remote.",
            GetRemoteUrls(expectedCatalog),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches one exact selected remote with allowlisted pruning and tag options.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="expectedCatalog">The exact complete remote catalog displayed to the user.</param>
    /// <param name="remote">The exact selected remote to fetch.</param>
    /// <param name="options">The validated typed fetch options.</param>
    /// <param name="cancellationToken">Signals fetch cancellation.</param>
    /// <returns>Git's exact successful transport output.</returns>
    internal async Task<GitOperationResult> FetchAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteInfo remote,
        FetchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.RemoteMutation,
            cancellationToken).ConfigureAwait(false);
        var liveRemote = await RevalidateRemoteAsync(
            workingDirectory,
            expectedCatalog,
            remote,
            cancellationToken).ConfigureAwait(false);
        var arguments = BuildFetchArguments(options);
        arguments.Add(ProcessArgument.Literal("--"));
        arguments.Add(ProcessArgument.Native(liveRemote.Name));
        return await RunTransportAsync(
            workingDirectory,
            [.. arguments],
            "Git could not fetch the selected remote.",
            GetRemoteUrls(expectedCatalog),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches every configured remote after proving the displayed complete catalog is current.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="expectedCatalog">The exact complete remote catalog displayed to the user.</param>
    /// <param name="options">The validated typed fetch options.</param>
    /// <param name="cancellationToken">Signals fetch-all cancellation.</param>
    /// <returns>Git's exact successful transport output.</returns>
    internal async Task<GitOperationResult> FetchAllAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        FetchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ArgumentNullException.ThrowIfNull(options);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.RemoteMutation,
            cancellationToken).ConfigureAwait(false);
        var liveCatalog = await CaptureAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        RequireMatchingCatalog(expectedCatalog, liveCatalog);
        var arguments = BuildFetchArguments(options);
        arguments.Insert(2, ProcessArgument.Literal("--all"));
        return await RunTransportAsync(
            workingDirectory,
            [.. arguments],
            "Git could not fetch all configured remotes.",
            GetRemoteUrls(expectedCatalog),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures Git's exact dry-run prune output for one stable selected remote.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="expectedCatalog">The exact complete remote catalog displayed to the user.</param>
    /// <param name="remote">The exact selected remote to preview.</param>
    /// <param name="cancellationToken">Signals prune-preview cancellation.</param>
    /// <returns>The exact revalidated prune confirmation plan.</returns>
    internal async Task<RemotePrunePlan> PreparePruneAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteInfo remote,
        CancellationToken cancellationToken)
    {
        var liveRemote = await RevalidateRemoteAsync(
            workingDirectory,
            expectedCatalog,
            remote,
            cancellationToken).ConfigureAwait(false);
        var preview = await RunTransportAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("remote"),
                ProcessArgument.Literal("prune"),
                ProcessArgument.Literal("--dry-run"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(liveRemote.Name),
            ],
            "Git could not preview pruning for the selected remote.",
            GetRemoteUrls(expectedCatalog),
            cancellationToken).ConfigureAwait(false);
        return new RemotePrunePlan(expectedCatalog, liveRemote, preview);
    }

    /// <summary>
    /// Prunes one exact selected remote after revalidating the complete confirmed catalog.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="plan">The exact dry-run confirmation displayed to the user.</param>
    /// <param name="cancellationToken">Signals prune cancellation.</param>
    /// <returns>Git's exact successful prune output.</returns>
    internal async Task<GitOperationResult> PruneAsync(
        CanonicalDirectory workingDirectory,
        RemotePrunePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.RemoteMutation,
            cancellationToken).ConfigureAwait(false);
        var liveRemote = await RevalidateRemoteAsync(
            workingDirectory,
            plan.Catalog,
            plan.Remote,
            cancellationToken).ConfigureAwait(false);
        var sensitiveUrls = GetRemoteUrls(plan.Catalog);
        var currentPreview = await RunTransportAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("remote"),
                ProcessArgument.Literal("prune"),
                ProcessArgument.Literal("--dry-run"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(liveRemote.Name),
            ],
            "Git could not revalidate pruning for the selected remote.",
            sensitiveUrls,
            cancellationToken).ConfigureAwait(false);
        if (!PrunePreviewMatches(plan.Preview, currentPreview))
        {
            throw new RepositoryPreconditionException(
                "Git's remote-prune preview changed after confirmation was prepared; review the new preview before pruning.");
        }

        return await RunTransportAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("remote"),
                ProcessArgument.Literal("prune"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(liveRemote.Name),
            ],
            "Git could not prune the selected remote.",
            sensitiveUrls,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteCatalog> ReadCatalogAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunReadAsync(
            workingDirectory,
            [ProcessArgument.Literal("remote")],
            MaximumConfigurationBytes,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not list configured remotes.");
        }

        var names = ParseRemoteNames(result.StandardOutput.Span);
        var remotes = ImmutableArray.CreateBuilder<RemoteInfo>(names.Length);
        foreach (var name in names)
        {
            var fetchUrls = await ReadConfigurationValuesAsync(
                workingDirectory,
                CreateRemoteKey(name, ".url"u8),
                cancellationToken).ConfigureAwait(false);
            var explicitPushUrls = await ReadConfigurationValuesAsync(
                workingDirectory,
                CreateRemoteKey(name, ".pushurl"u8),
                cancellationToken).ConfigureAwait(false);
            var pushUrls = explicitPushUrls.IsEmpty ? fetchUrls : explicitPushUrls;
            remotes.Add(new RemoteInfo(name, fetchUrls, pushUrls));
        }

        return new RemoteCatalog(remotes.ToImmutable());
    }

    private async Task<ImmutableArray<RemoteUrl>> ReadConfigurationValuesAsync(
        CanonicalDirectory workingDirectory,
        GitConfigurationKey key,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("config"),
                ProcessArgument.Literal("--null"),
                ProcessArgument.Literal("--get-all"),
                ProcessArgument.Native(key),
            ],
            workingDirectory,
            _environmentFactory.CreateConfigurationReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumConfigurationBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1 && result.StandardOutput.IsEmpty)
        {
            return [];
        }

        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not read remote URL configuration.");
        }

        return ParseRemoteUrls(result.StandardOutput.Span);
    }

    private async Task<RemoteInfo> RevalidateRemoteAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteInfo remote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ArgumentNullException.ThrowIfNull(remote);
        var liveCatalog = await CaptureAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        RequireMatchingCatalog(expectedCatalog, liveCatalog);
        var liveRemote = liveCatalog.Find(remote.Name);
        if (liveRemote is null || !liveRemote.Matches(remote))
        {
            throw new RepositoryPreconditionException(
                "The selected remote changed after it was displayed; refresh and retry.");
        }

        return liveRemote;
    }

    private async Task<ProcessResult> RunReadAsync(
        CanonicalDirectory workingDirectory,
        IReadOnlyList<ProcessArgument> arguments,
        int maximumOutputBytes,
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
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(maximumOutputBytes, MaximumErrorBytes));
        return await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private Task<GitOperationResult> RunMutationAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<ProcessArgument> arguments,
        string fallbackError,
        IReadOnlyList<RemoteUrl> sensitiveUrls,
        CancellationToken cancellationToken)
        => RunAsync(
            workingDirectory,
            arguments,
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            fallbackError,
            sensitiveUrls,
            cancellationToken);

    private async Task<GitOperationResult> RunTransportAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<ProcessArgument> arguments,
        string fallbackError,
        IReadOnlyList<RemoteUrl> sensitiveUrls,
        CancellationToken cancellationToken)
    {
        await using var promptOperation = _credentialPromptBroker.StartOperation(
            fallbackError,
            cancellationToken);
        return await RunAsync(
            workingDirectory,
            arguments,
            promptOperation.ConfigureEnvironment(_environmentFactory.CreateTransportEnvironment()),
            fallbackError,
            sensitiveUrls,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitOperationResult> RunAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<ProcessArgument> arguments,
        ChildEnvironment environment,
        string fallbackError,
        IReadOnlyList<RemoteUrl> sensitiveUrls,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [ProcessArgument.Literal("--no-pager"), .. arguments],
            workingDirectory,
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumTransportOutputBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, fallbackError, sensitiveUrls);
        }

        return new GitOperationResult(result.StandardOutput, result.StandardError);
    }

    private static List<ProcessArgument> BuildFetchArguments(FetchOptions options)
    {
        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("fetch"),
            ProcessArgument.Literal("--progress"),
        };
        var prune = options.Prune switch
        {
            GitOptionOverride.Configured => null,
            GitOptionOverride.Enabled => "--prune",
            GitOptionOverride.Disabled => "--no-prune",
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
        if (prune is not null)
        {
            arguments.Add(ProcessArgument.Literal(prune));
        }

        var tags = options.Tags switch
        {
            FetchTagMode.Configured => null,
            FetchTagMode.All => "--tags",
            FetchTagMode.None => "--no-tags",
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
        if (tags is not null)
        {
            arguments.Add(ProcessArgument.Literal(tags));
        }

        return arguments;
    }

    private static ImmutableArray<RemoteName> ParseRemoteNames(ReadOnlySpan<byte> output)
    {
        if (output.IsEmpty)
        {
            return [];
        }

        var names = ImmutableArray.CreateBuilder<RemoteName>();
        while (!output.IsEmpty)
        {
            var terminator = output.IndexOf((byte)'\n');
            if (terminator < 0)
            {
                throw new InvalidDataException("Git remote output ended before a line terminator.");
            }

            if (terminator == 0 || terminator > MaximumRemoteNameBytes)
            {
                throw new InvalidDataException("Git remote output contains an invalid remote name.");
            }

            var name = RemoteName.FromBytes(output[..terminator]);
            if (names.Count == MaximumRemoteCount)
            {
                throw new InvalidDataException("Git returned more remotes than the supported limit.");
            }

            if (names.Count > 0 && names[^1].CompareTo(name) >= 0)
            {
                throw new InvalidDataException("Git returned duplicate or unordered remote names.");
            }

            names.Add(name);
            output = output[(terminator + 1)..];
        }

        return names.ToImmutable();
    }

    private static ImmutableArray<RemoteUrl> ParseRemoteUrls(ReadOnlySpan<byte> output)
    {
        if (output.IsEmpty)
        {
            return [];
        }

        var urls = ImmutableArray.CreateBuilder<RemoteUrl>();
        while (!output.IsEmpty)
        {
            var terminator = output.IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidDataException("Git remote URL output ended before a NUL terminator.");
            }

            var value = output[..terminator];
            if (value.IsEmpty)
            {
                urls.Clear();
            }
            else
            {
                urls.Add(RemoteUrl.FromBytes(value));
            }

            output = output[(terminator + 1)..];
        }

        return urls.ToImmutable();
    }

    private static GitConfigurationKey CreateRemoteKey(RemoteName name, ReadOnlySpan<byte> suffix)
    {
        var bytes = new byte["remote."u8.Length + name.GetBytes().Length + suffix.Length];
        "remote."u8.CopyTo(bytes);
        name.GetBytes().CopyTo(bytes.AsSpan("remote."u8.Length));
        suffix.CopyTo(bytes.AsSpan("remote."u8.Length + name.GetBytes().Length));
        return GitConfigurationKey.FromBytes(bytes);
    }

    private static void RequireMatchingCatalog(RemoteCatalog expected, RemoteCatalog live)
    {
        if (!expected.Matches(live))
        {
            throw new RepositoryPreconditionException(
                "Remote names or URLs changed after the remote view was prepared; refresh and retry.");
        }
    }

    private static ImmutableArray<RemoteUrl> GetRemoteUrls(RemoteCatalog catalog)
    {
        var urls = ImmutableArray.CreateBuilder<RemoteUrl>();
        foreach (var remote in catalog.Remotes)
        {
            urls.AddRange(remote.FetchUrls);
            urls.AddRange(remote.PushUrls);
        }

        return urls.ToImmutable();
    }

    /// <summary>
    /// Compares prune state without depending on Git's stream, heading, path, order, or host-newline presentation.
    /// </summary>
    /// <param name="expected">The preview shown before confirmation.</param>
    /// <param name="actual">The preview captured immediately before pruning.</param>
    /// <returns><see langword="true"/> when both previews contain the same exact state-line multiset.</returns>
    internal static bool PrunePreviewMatches(GitOperationResult expected, GitOperationResult actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var expectedLines = GetSortedStateLines(expected);
        var actualLines = GetSortedStateLines(actual);
        if (expectedLines.Count != actualLines.Count)
        {
            return false;
        }

        for (var index = 0; index < expectedLines.Count; index++)
        {
            if (!expectedLines[index].AsSpan().SequenceEqual(actualLines[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static List<byte[]> GetSortedStateLines(GitOperationResult preview)
    {
        var lines = new List<byte[]>();
        AddStateLines(preview.StandardOutput.Span, lines);
        AddStateLines(preview.StandardError.Span, lines);
        lines.Sort(static (left, right) => left.AsSpan().SequenceCompareTo(right));
        return lines;
    }

    private static void AddStateLines(ReadOnlySpan<byte> output, List<byte[]> lines)
    {
        while (!output.IsEmpty)
        {
            var terminator = output.IndexOf((byte)'\n');
            var line = terminator < 0 ? output : output[..terminator];
            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (!line.IsEmpty &&
                !line.StartsWith("Pruning "u8) &&
                !line.StartsWith("URL: "u8))
            {
                lines.Add(line.ToArray());
            }

            if (terminator < 0)
            {
                break;
            }

            output = output[(terminator + 1)..];
        }
    }

    private static GitCommandException CreateCommandException(ProcessResult result, string fallbackError)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        return new GitCommandException(
            result.ExitCode,
            string.IsNullOrEmpty(error) ? fallbackError : error);
    }

    private static GitCommandException CreateCommandException(
        ProcessResult result,
        string fallbackError,
        IReadOnlyList<RemoteUrl> sensitiveUrls)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        foreach (var url in sensitiveUrls)
        {
            error = url.RedactFrom(error);
        }

        return new GitCommandException(
            result.ExitCode,
            string.IsNullOrEmpty(error) ? fallbackError : error);
    }
}
