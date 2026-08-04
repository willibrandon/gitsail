using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Plans and executes Git-resolved default pushes against exact local and advertised remote state.
/// </summary>
internal sealed class PushService
{
    private const int MaximumOutputBytes = 256 * 1024 * 1024;
    private const int MaximumErrorBytes = 64 * 1024 * 1024;
    private const int MaximumAdvertisementLineBytes = 1024 * 1024;
    private const int MaximumAdvertisedRefs = 1_000_000;
    private const int MaximumStableCaptureAttempts = 3;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly RemoteService _remoteService;
    private readonly CredentialPromptBroker _credentialPromptBroker;

    /// <summary>
    /// Initializes exact push planning and execution over shared repository services.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The repository mutation coordinator.</param>
    /// <param name="remoteService">The stable configured-remote service.</param>
    /// <param name="credentialPromptBroker">The operation-scoped authenticated credential broker.</param>
    internal PushService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        RepositoryMutationCoordinator coordinator,
        RemoteService remoteService,
        CredentialPromptBroker credentialPromptBroker)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(remoteService);
        ArgumentNullException.ThrowIfNull(credentialPromptBroker);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _coordinator = coordinator;
        _remoteService = remoteService;
        _credentialPromptBroker = credentialPromptBroker;
    }

    /// <summary>
    /// Resolves Git's complete default push behavior into one exact stable confirmation plan.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="expectedCatalog">The exact complete remote catalog displayed to the user.</param>
    /// <param name="remote">The exact selected configured destination remote.</param>
    /// <param name="followTags">The configured or explicit reachable annotated-tag behavior.</param>
    /// <param name="cancellationToken">Signals push planning cancellation.</param>
    /// <returns>The exact source, destination, OID, relationship, and commit-count plan.</returns>
    internal Task<PushPlan> PrepareAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteInfo remote,
        GitOptionOverride followTags,
        CancellationToken cancellationToken)
        => PrepareCoreAsync(
            workingDirectory,
            expectedCatalog,
            remote,
            explicitRefSpecs: default,
            followTags,
            cancellationToken);

    /// <summary>
    /// Captures one stable complete list of exact local tag refs for interactive selection.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="cancellationToken">Signals tag-catalog cancellation.</param>
    /// <returns>Every exact local tag ref in bytewise order.</returns>
    internal async Task<ImmutableArray<RefName>> CaptureLocalTagsAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        for (var attempt = 0; attempt < MaximumStableCaptureAttempts; attempt++)
        {
            var first = await ReadLocalTagsAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            var second = await ReadLocalTagsAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            if (first.Span.SequenceEqual(second.Span))
            {
                return ParseReferenceList(first.Span, "refs/tags/"u8, "local tag");
            }
        }

        throw new RepositoryPreconditionException(
            "Local tags continued changing while GitSail prepared the selection; retry the refresh.");
    }

    /// <summary>
    /// Captures the stable union of exact branch refs advertised by every selected push destination.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="expectedCatalog">The exact complete remote catalog displayed to the user.</param>
    /// <param name="remote">The exact selected configured destination remote.</param>
    /// <param name="cancellationToken">Signals remote-branch catalog cancellation.</param>
    /// <returns>Every advertised exact branch ref in bytewise order.</returns>
    internal async Task<ImmutableArray<RefName>> CaptureRemoteBranchesAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteInfo remote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ArgumentNullException.ThrowIfNull(remote);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.RemoteMutation,
            cancellationToken).ConfigureAwait(false);
        var firstRemote = await RevalidateRemoteAsync(
            workingDirectory,
            expectedCatalog,
            remote,
            cancellationToken).ConfigureAwait(false);
        RequirePushUrls(firstRemote);
        var firstUrls = await CaptureEffectivePushUrlsAsync(
            workingDirectory,
            firstRemote,
            cancellationToken).ConfigureAwait(false);
        var firstAdvertisements = await CaptureAdvertisementsAsync(
            workingDirectory,
            firstUrls,
            cancellationToken).ConfigureAwait(false);
        var finalRemote = await RevalidateRemoteAsync(
            workingDirectory,
            expectedCatalog,
            firstRemote,
            cancellationToken).ConfigureAwait(false);
        var finalUrls = await CaptureEffectivePushUrlsAsync(
            workingDirectory,
            finalRemote,
            cancellationToken).ConfigureAwait(false);
        var finalAdvertisements = await CaptureAdvertisementsAsync(
            workingDirectory,
            finalUrls,
            cancellationToken).ConfigureAwait(false);
        if (!UrlsMatch(firstUrls, finalUrls) ||
            !AdvertisementsMatch(firstAdvertisements, finalAdvertisements))
        {
            throw new RepositoryPreconditionException(
                "Remote branches changed while GitSail prepared the selection; retry the refresh.");
        }

        return [.. finalAdvertisements
            .SelectMany(static advertisement => advertisement.Refs.Keys)
            .Where(static referenceName => referenceName.GetBytes().StartsWith("refs/heads/"u8))
            .Distinct()
            .Order()];
    }

    /// <summary>
    /// Prepares one exact local tag update to the same fully qualified tag on every destination.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="expectedCatalog">The exact complete remote catalog displayed to the user.</param>
    /// <param name="remote">The exact selected configured destination remote.</param>
    /// <param name="tag">The exact fully qualified local tag selected by the user.</param>
    /// <param name="cancellationToken">Signals tag-push planning cancellation.</param>
    /// <returns>The exact leased tag update plan.</returns>
    internal Task<PushPlan> PrepareTagAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteInfo remote,
        RefName tag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tag);
        RequireReferenceNamespace(tag, "refs/tags/"u8, "tag");
        return PrepareCoreAsync(
            workingDirectory,
            expectedCatalog,
            remote,
            [new PushRefSpec(tag, tag)],
            GitOptionOverride.Disabled,
            cancellationToken);
    }

    /// <summary>
    /// Prepares one exact remote branch deletion and requires it to exist on at least one destination.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="expectedCatalog">The exact complete remote catalog displayed to the user.</param>
    /// <param name="remote">The exact selected configured destination remote.</param>
    /// <param name="branch">The exact fully qualified advertised branch selected by the user.</param>
    /// <param name="cancellationToken">Signals deletion planning cancellation.</param>
    /// <returns>The exact leased remote branch deletion plan.</returns>
    internal async Task<PushPlan> PrepareRemoteBranchDeletionAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteInfo remote,
        RefName branch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branch);
        RequireReferenceNamespace(branch, "refs/heads/"u8, "remote branch");
        var plan = await PrepareCoreAsync(
            workingDirectory,
            expectedCatalog,
            remote,
            [new PushRefSpec(source: null, branch)],
            GitOptionOverride.Disabled,
            cancellationToken).ConfigureAwait(false);
        if (plan.Updates[0].Destinations.All(static destination =>
            destination.ExpectedObjectId is null))
        {
            throw new RepositoryPreconditionException(
                "The selected remote branch no longer exists on any configured push destination.");
        }

        return plan;
    }

    private async Task<PushPlan> PrepareCoreAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteInfo remote,
        ImmutableArray<PushRefSpec> explicitRefSpecs,
        GitOptionOverride followTags,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ArgumentNullException.ThrowIfNull(remote);
        if (!Enum.IsDefined(followTags))
        {
            throw new ArgumentOutOfRangeException(nameof(followTags));
        }

        var isDefaultPush = explicitRefSpecs.IsDefault;
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.RemoteMutation,
            cancellationToken).ConfigureAwait(false);
        var liveRemote = await RevalidateRemoteAsync(
            workingDirectory,
            expectedCatalog,
            remote,
            cancellationToken).ConfigureAwait(false);
        RequirePushUrls(liveRemote);
        var firstEffectiveUrls = await CaptureEffectivePushUrlsAsync(
            workingDirectory,
            liveRemote,
            cancellationToken).ConfigureAwait(false);
        var sensitiveUrls = GetRemoteUrls(expectedCatalog).AddRange(firstEffectiveUrls);
        var dryRun = await RunRawAsync(
            workingDirectory,
            BuildDryRunArguments(liveRemote.Name, followTags, explicitRefSpecs),
            cancellationToken).ConfigureAwait(false);
        if (dryRun.ExitCode != 0)
        {
            throw CreateCommandException(
                dryRun,
                isDefaultPush
                    ? "Git could not resolve the selected remote's default push behavior."
                    : "Git could not validate the selected explicit remote ref update.",
                sensitiveUrls);
        }

        var porcelain = PushPorcelainParser.Parse(dryRun.StandardOutput.Span);
        if (porcelain.RefSpecs.IsEmpty)
        {
            throw new PushOperationException(
                "Git's configured default push selected no refs; choose an explicit branch or change push.default.");
        }

        if (!isDefaultPush && !porcelain.RefSpecs.SequenceEqual(explicitRefSpecs))
        {
            throw new InvalidDataException(
                "Git did not preserve the complete explicit ref update during dry-run validation.");
        }

        var upstreamName = isDefaultPush
            ? await CaptureCurrentUpstreamAsync(
                workingDirectory,
                cancellationToken).ConfigureAwait(false)
            : null;
        var firstAdvertisements = await CaptureAdvertisementsAsync(
            workingDirectory,
            firstEffectiveUrls,
            cancellationToken).ConfigureAwait(false);
        var sourceObjectIds = await CaptureSourceObjectIdsAsync(
            workingDirectory,
            porcelain.RefSpecs,
            cancellationToken).ConfigureAwait(false);
        var updates = await BuildUpdatesAsync(
            workingDirectory,
            porcelain.RefSpecs,
            sourceObjectIds,
            firstAdvertisements,
            cancellationToken).ConfigureAwait(false);

        var finalRemote = await RevalidateRemoteAsync(
            workingDirectory,
            expectedCatalog,
            liveRemote,
            cancellationToken).ConfigureAwait(false);
        var finalEffectiveUrls = await CaptureEffectivePushUrlsAsync(
            workingDirectory,
            finalRemote,
            cancellationToken).ConfigureAwait(false);
        if (!UrlsMatch(firstEffectiveUrls, finalEffectiveUrls))
        {
            throw new RepositoryPreconditionException(
                "An effective push URL changed while GitSail prepared the push; review a new plan.");
        }

        var finalSourceObjectIds = await CaptureSourceObjectIdsAsync(
            workingDirectory,
            porcelain.RefSpecs,
            cancellationToken).ConfigureAwait(false);
        if (!SourceObjectIdsMatch(sourceObjectIds, finalSourceObjectIds))
        {
            throw new RepositoryPreconditionException(
                "A local source ref changed while GitSail prepared the push; review a new plan.");
        }

        var finalAdvertisements = await CaptureAdvertisementsAsync(
            workingDirectory,
            finalEffectiveUrls,
            cancellationToken).ConfigureAwait(false);
        if (!AdvertisementsMatch(firstAdvertisements, finalAdvertisements))
        {
            throw new RepositoryPreconditionException(
                "A remote destination changed while GitSail prepared the push; review a new plan.");
        }

        return new PushPlan(
            expectedCatalog,
            finalRemote,
            updates,
            upstreamName,
            isDefaultPush && porcelain.WouldSetUpstream,
            followTags);
    }

    /// <summary>
    /// Executes one frozen push plan after exact catalog, source, and destination revalidation.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="plan">The exact push confirmation displayed to the user.</param>
    /// <param name="options">The validated safety and upstream choices confirmed by the user.</param>
    /// <param name="cancellationToken">Signals push cancellation.</param>
    /// <returns>Git's exact successful push output.</returns>
    internal async Task<GitOperationResult> PushAsync(
        CanonicalDirectory workingDirectory,
        PushPlan plan,
        PushOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        if (options.FollowTags != plan.FollowTags)
        {
            throw new RepositoryPreconditionException(
                "Follow-tags behavior changed after the push plan was prepared; review a new plan.");
        }

        if (options.SafetyMode == PushSafetyMode.Normal &&
            (plan.RequiresForce || plan.IncludesDeletion))
        {
            throw new PushOperationException(
                "This plan contains a non-fast-forward update or deletion and requires explicit lease confirmation.");
        }

        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.RemoteMutation,
            cancellationToken).ConfigureAwait(false);
        var liveRemote = await RevalidateRemoteAsync(
            workingDirectory,
            plan.Catalog,
            plan.Remote,
            cancellationToken).ConfigureAwait(false);
        var sourceObjectIds = await CaptureSourceObjectIdsAsync(
            workingDirectory,
            [.. plan.Updates.Select(static update => update.RefSpec)],
            cancellationToken).ConfigureAwait(false);
        RequirePlannedSourceObjectIds(plan, sourceObjectIds);
        var effectiveUrls = await CaptureEffectivePushUrlsAsync(
            workingDirectory,
            liveRemote,
            cancellationToken).ConfigureAwait(false);
        if (!UrlsMatch(GetPlannedEffectiveUrls(plan), effectiveUrls))
        {
            throw new RepositoryPreconditionException(
                "An effective push URL changed after confirmation; review a new plan.");
        }

        var advertisements = await CaptureAdvertisementsAsync(
            workingDirectory,
            effectiveUrls,
            cancellationToken).ConfigureAwait(false);
        RequirePlannedAdvertisements(plan, advertisements);

        var sensitiveUrls = GetRemoteUrls(plan.Catalog).AddRange(effectiveUrls);
        var standardOutput = new ArrayBufferWriter<byte>();
        var standardError = new ArrayBufferWriter<byte>();
        for (var destinationIndex = 0; destinationIndex < effectiveUrls.Length; destinationIndex++)
        {
            var result = await RunRawAsync(
                workingDirectory,
                BuildExecutionArguments(
                    plan,
                    liveRemote,
                    effectiveUrls[destinationIndex],
                    destinationIndex,
                    options,
                    setUpstream: destinationIndex == 0 &&
                        (options.SetUpstream || plan.WouldSetUpstream)),
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw CreateCommandException(
                    result,
                    "Git could not complete one destination of the exact push plan; earlier configured URLs may already be updated.",
                    sensitiveUrls);
            }

            AppendBounded(
                standardOutput,
                result.StandardOutput.Span,
                MaximumOutputBytes,
                "standard output");
            AppendBounded(
                standardError,
                result.StandardError.Span,
                MaximumErrorBytes,
                "standard error");
        }

        var finalAdvertisements = await CaptureAdvertisementsAsync(
            workingDirectory,
            effectiveUrls,
            cancellationToken).ConfigureAwait(false);
        RequireSuccessfulResult(plan, finalAdvertisements);
        return new GitOperationResult(
            standardOutput.WrittenMemory.ToArray(),
            standardError.WrittenMemory.ToArray());
    }

    private async Task<RemoteInfo> RevalidateRemoteAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteInfo remote,
        CancellationToken cancellationToken)
    {
        var liveCatalog = await _remoteService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!expectedCatalog.Matches(liveCatalog))
        {
            throw new RepositoryPreconditionException(
                "Remote names or URLs changed after the push view was prepared; refresh and retry.");
        }

        var liveRemote = liveCatalog.Find(remote.Name);
        if (liveRemote is null || !liveRemote.Matches(remote))
        {
            throw new RepositoryPreconditionException(
                "The selected push remote changed after it was displayed; refresh and retry.");
        }

        return liveRemote;
    }

    private async Task<ImmutableArray<(RemoteUrl Url, ImmutableDictionary<RefName, ObjectId> Refs)>>
        CaptureAdvertisementsAsync(
            CanonicalDirectory workingDirectory,
            ImmutableArray<RemoteUrl> urls,
            CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<(
            RemoteUrl Url,
            ImmutableDictionary<RefName, ObjectId> Refs)>(urls.Length);
        foreach (var url in urls)
        {
            if (url.GetBytes().IsEmpty)
            {
                throw new PushOperationException("The selected remote has an empty push URL.");
            }

            var invocationResult = await RunRawAsync(
                workingDirectory,
                [
                    ProcessArgument.Literal("ls-remote"),
                    ProcessArgument.Literal("--refs"),
                    ProcessArgument.Literal("--"),
                    ProcessArgument.Native(url),
                ],
                cancellationToken).ConfigureAwait(false);
            if (invocationResult.ExitCode != 0)
            {
                throw CreateCommandException(
                    invocationResult,
                    "Git could not read one configured push destination.",
                    urls);
            }

            result.Add((url, ParseAdvertisement(invocationResult.StandardOutput.Span)));
        }

        return result.ToImmutable();
    }

    private async Task<ImmutableArray<RemoteUrl>> CaptureEffectivePushUrlsAsync(
        CanonicalDirectory workingDirectory,
        RemoteInfo remote,
        CancellationToken cancellationToken)
    {
        var rawUrls = remote.PushUrls;
        var result = await RunRawAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("remote"),
                ProcessArgument.Literal("get-url"),
                ProcessArgument.Literal("--push"),
                ProcessArgument.Literal("--all"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(remote.Name),
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(
                result,
                "Git could not resolve the selected remote's effective push URLs.",
                rawUrls);
        }

        var urls = ParseEffectiveUrls(result.StandardOutput.Span);
        if (urls.Length != rawUrls.Length)
        {
            throw new InvalidDataException(
                "Git returned an ambiguous effective push URL list; embedded line breaks are not accepted.");
        }

        return urls;
    }

    private async Task<ImmutableDictionary<RefName, ObjectId>> CaptureSourceObjectIdsAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<PushRefSpec> refSpecs,
        CancellationToken cancellationToken)
    {
        var result = ImmutableDictionary.CreateBuilder<RefName, ObjectId>();
        foreach (var source in refSpecs
            .Select(static refSpec => refSpec.Source)
            .OfType<RefName>()
            .Distinct())
        {
            result.Add(source, await ResolveObjectIdAsync(
                workingDirectory,
                source,
                cancellationToken).ConfigureAwait(false));
        }

        return result.ToImmutable();
    }

    private async Task<ImmutableArray<PushUpdatePlan>> BuildUpdatesAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<PushRefSpec> refSpecs,
        ImmutableDictionary<RefName, ObjectId> sourceObjectIds,
        ImmutableArray<(RemoteUrl Url, ImmutableDictionary<RefName, ObjectId> Refs)> advertisements,
        CancellationToken cancellationToken)
    {
        var updates = ImmutableArray.CreateBuilder<PushUpdatePlan>(refSpecs.Length);
        foreach (var refSpec in refSpecs)
        {
            var sourceObjectId = refSpec.Source is null ? null : sourceObjectIds[refSpec.Source];
            var destinations = ImmutableArray.CreateBuilder<PushDestinationExpectation>(advertisements.Length);
            foreach (var advertisement in advertisements)
            {
                _ = advertisement.Refs.TryGetValue(refSpec.Destination, out var expectedObjectId);
                var relationship = await GetRelationshipAsync(
                    workingDirectory,
                    refSpec,
                    sourceObjectId,
                    expectedObjectId,
                    advertisement.Url,
                    cancellationToken).ConfigureAwait(false);
                var commitCount = await GetCommitCountAsync(
                    workingDirectory,
                    sourceObjectId,
                    expectedObjectId,
                    relationship,
                    advertisement.Url,
                    cancellationToken).ConfigureAwait(false);
                destinations.Add(new PushDestinationExpectation(
                    advertisement.Url,
                    expectedObjectId,
                    relationship,
                    commitCount));
            }

            updates.Add(new PushUpdatePlan(refSpec, sourceObjectId, destinations.ToImmutable()));
        }

        return updates.ToImmutable();
    }

    private async Task<PushRelationship> GetRelationshipAsync(
        CanonicalDirectory workingDirectory,
        PushRefSpec refSpec,
        ObjectId? sourceObjectId,
        ObjectId? expectedObjectId,
        RemoteUrl url,
        CancellationToken cancellationToken)
    {
        if (sourceObjectId is null)
        {
            return PushRelationship.Delete;
        }

        if (expectedObjectId is null)
        {
            return PushRelationship.New;
        }

        if (sourceObjectId.Equals(expectedObjectId))
        {
            return PushRelationship.UpToDate;
        }

        if (!refSpec.Destination.GetBytes().StartsWith("refs/heads/"u8))
        {
            return PushRelationship.NonFastForward;
        }

        await EnsureObjectAvailableAsync(
            workingDirectory,
            expectedObjectId,
            url,
            cancellationToken).ConfigureAwait(false);
        var result = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("merge-base"),
                ProcessArgument.Literal("--is-ancestor"),
                ProcessArgument.Native(expectedObjectId),
                ProcessArgument.Native(sourceObjectId),
            ],
            1024,
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => PushRelationship.FastForward,
            1 => PushRelationship.NonFastForward,
            _ => throw CreateCommandException(
                result,
                "Git could not compare the planned source and destination commits.",
                [url]),
        };
    }

    private async Task<long> GetCommitCountAsync(
        CanonicalDirectory workingDirectory,
        ObjectId? sourceObjectId,
        ObjectId? expectedObjectId,
        PushRelationship relationship,
        RemoteUrl url,
        CancellationToken cancellationToken)
    {
        if (sourceObjectId is null || relationship is PushRelationship.UpToDate or PushRelationship.Delete)
        {
            return 0;
        }

        if (expectedObjectId is not null)
        {
            await EnsureObjectAvailableAsync(
                workingDirectory,
                expectedObjectId,
                url,
                cancellationToken).ConfigureAwait(false);
        }

        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("rev-list"),
            ProcessArgument.Literal("--count"),
            ProcessArgument.Native(sourceObjectId),
        };
        if (expectedObjectId is not null)
        {
            arguments.Add(ProcessArgument.Literal("--not"));
            arguments.Add(ProcessArgument.Native(expectedObjectId));
        }

        var result = await RunReadAsync(
            workingDirectory,
            [.. arguments],
            1024,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new PushOperationException(
                "A planned push source is not commit-reachable; review it through the dedicated tag or ref workflow.");
        }

        var text = Encoding.ASCII.GetString(result.StandardOutput.Span).Trim();
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0)
        {
            throw new InvalidDataException("Git returned an invalid push commit count.");
        }

        return count;
    }

    private async Task EnsureObjectAvailableAsync(
        CanonicalDirectory workingDirectory,
        ObjectId objectId,
        RemoteUrl url,
        CancellationToken cancellationToken)
    {
        var exists = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("cat-file"),
                ProcessArgument.Literal("-e"),
                ProcessArgument.Native(objectId),
            ],
            1024,
            cancellationToken).ConfigureAwait(false);
        if (exists.ExitCode == 0)
        {
            return;
        }

        var fetch = await RunRawAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("fetch"),
                ProcessArgument.Literal("--no-tags"),
                ProcessArgument.Literal("--no-write-fetch-head"),
                ProcessArgument.Literal("--no-recurse-submodules"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(url),
                ProcessArgument.Native(objectId),
            ],
            cancellationToken).ConfigureAwait(false);
        if (fetch.ExitCode != 0)
        {
            throw CreateCommandException(
                fetch,
                "Git could not obtain the advertised remote object needed for exact push planning.",
                [url]);
        }
    }

    private async Task<ObjectId> ResolveObjectIdAsync(
        CanonicalDirectory workingDirectory,
        RefName referenceName,
        CancellationToken cancellationToken)
    {
        var result = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("rev-parse"),
                ProcessArgument.Literal("--verify"),
                ProcessArgument.Literal("--end-of-options"),
                ProcessArgument.Native(referenceName),
            ],
            4096,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(
                result,
                "Git could not resolve a planned push source ref.",
                []);
        }

        return ParseSingleObjectId(result.StandardOutput.Span, "push source");
    }

    private async Task<RefName?> CaptureCurrentUpstreamAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var headResult = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("symbolic-ref"),
                ProcessArgument.Literal("--quiet"),
                ProcessArgument.Literal("HEAD"),
            ],
            1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        if (headResult.ExitCode == 1)
        {
            return null;
        }

        if (headResult.ExitCode != 0)
        {
            throw CreateCommandException(headResult, "Git could not resolve the current branch.", []);
        }

        var head = RefName.FromBytes(TrimSingleLine(headResult.StandardOutput.Span, "current branch"));
        var upstreamResult = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("for-each-ref"),
                ProcessArgument.Literal("--format=%(upstream)"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(head),
            ],
            1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        if (upstreamResult.ExitCode != 0)
        {
            throw CreateCommandException(
                upstreamResult,
                "Git could not resolve the current branch upstream.",
                []);
        }

        var upstream = TrimSingleLine(upstreamResult.StandardOutput.Span, "current branch upstream");
        return upstream.IsEmpty ? null : RefName.FromBytes(upstream);
    }

    private async Task<ProcessResult> RunReadAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<ProcessArgument> arguments,
        int maximumOutputBytes,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [ProcessArgument.Literal("--no-pager"), .. arguments],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(maximumOutputBytes, MaximumErrorBytes));
        return await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessResult> RunRawAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<ProcessArgument> arguments,
        CancellationToken cancellationToken)
    {
        await using var promptOperation = _credentialPromptBroker.StartOperation(
            "Git remote advertisement or push",
            cancellationToken);
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [ProcessArgument.Literal("--no-pager"), .. arguments],
            workingDirectory,
            promptOperation.ConfigureEnvironment(_environmentFactory.CreateTransportEnvironment()),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumOutputBytes, MaximumErrorBytes));
        return await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReadOnlyMemory<byte>> ReadLocalTagsAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("for-each-ref"),
                ProcessArgument.Literal("--sort=refname"),
                ProcessArgument.Literal("--format=%(refname)%00"),
                ProcessArgument.Literal("refs/tags/"),
            ],
            MaximumOutputBytes,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not enumerate local tags.", []);
        }

        return result.StandardOutput;
    }

    private static ImmutableArray<ProcessArgument> BuildDryRunArguments(
        RemoteName remoteName,
        GitOptionOverride followTags,
        ImmutableArray<PushRefSpec> explicitRefSpecs)
    {
        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("push"),
            ProcessArgument.Literal("--porcelain"),
            ProcessArgument.Literal("--dry-run"),
            ProcessArgument.Literal("--force"),
            ProcessArgument.Literal("--no-progress"),
        };
        var followTagsArgument = followTags switch
        {
            GitOptionOverride.Configured => null,
            GitOptionOverride.Enabled => "--follow-tags",
            GitOptionOverride.Disabled => "--no-follow-tags",
            _ => throw new ArgumentOutOfRangeException(nameof(followTags)),
        };
        if (followTagsArgument is not null)
        {
            arguments.Add(ProcessArgument.Literal(followTagsArgument));
        }

        arguments.Add(ProcessArgument.Literal("--"));
        arguments.Add(ProcessArgument.Native(remoteName));
        if (!explicitRefSpecs.IsDefault)
        {
            arguments.AddRange(explicitRefSpecs.Select(ProcessArgument.Native));
        }

        return [.. arguments];
    }

    private static ImmutableArray<ProcessArgument> BuildExecutionArguments(
        PushPlan plan,
        RemoteInfo remote,
        RemoteUrl effectiveUrl,
        int destinationIndex,
        PushOptions options,
        bool setUpstream)
    {
        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("-c"),
            CreateRemoteConfigurationArgument(remote.Name, ".pushurl"u8, []),
            ProcessArgument.Literal("-c"),
            CreateRemoteConfigurationArgument(remote.Name, ".pushurl"u8, effectiveUrl.GetBytes()),
            ProcessArgument.Literal("-c"),
            CreateRemoteConfigurationArgument(remote.Name, ".mirror"u8, "false"u8),
            ProcessArgument.Literal("push"),
            ProcessArgument.Literal("--porcelain"),
            ProcessArgument.Literal("--progress"),
            ProcessArgument.Literal("--no-follow-tags"),
        };
        if (setUpstream)
        {
            arguments.Add(ProcessArgument.Literal("--set-upstream"));
        }

        if (options.SafetyMode == PushSafetyMode.Force)
        {
            arguments.Add(ProcessArgument.Literal("--force"));
        }
        else
        {
            foreach (var update in plan.Updates)
            {
                arguments.Add(ProcessArgument.Native(new PushLease(
                    update.RefSpec.Destination,
                    update.Destinations[destinationIndex].ExpectedObjectId)));
            }
        }

        arguments.Add(ProcessArgument.Literal("--"));
        arguments.Add(ProcessArgument.Native(remote.Name));
        arguments.AddRange(plan.Updates.Select(static update => ProcessArgument.Native(update.RefSpec)));
        return [.. arguments];
    }

    private static ProcessArgument CreateRemoteConfigurationArgument(
        RemoteName remoteName,
        ReadOnlySpan<byte> suffix,
        ReadOnlySpan<byte> value)
    {
        var prefix = "remote."u8;
        var bytes = new byte[
            prefix.Length + remoteName.GetBytes().Length + suffix.Length + 1 + value.Length];
        prefix.CopyTo(bytes);
        remoteName.GetBytes().CopyTo(bytes.AsSpan(prefix.Length));
        suffix.CopyTo(bytes.AsSpan(prefix.Length + remoteName.GetBytes().Length));
        var separator = prefix.Length + remoteName.GetBytes().Length + suffix.Length;
        bytes[separator] = (byte)'=';
        value.CopyTo(bytes.AsSpan(separator + 1));
        return OperatingSystem.IsWindows()
            ? ProcessArgument.Literal(s_strictUtf8.GetString(bytes))
            : ProcessArgument.FromUnixBytes(bytes);
    }

    private static ImmutableDictionary<RefName, ObjectId> ParseAdvertisement(ReadOnlySpan<byte> output)
    {
        var refs = ImmutableDictionary.CreateBuilder<RefName, ObjectId>();
        while (!output.IsEmpty)
        {
            var terminator = output.IndexOf((byte)'\n');
            if (terminator < 0)
            {
                throw new InvalidDataException("Git ls-remote output ended before a line terminator.");
            }

            if (terminator > MaximumAdvertisementLineBytes)
            {
                throw new InvalidDataException("Git ls-remote output contains an overlong record.");
            }

            var line = output[..terminator];
            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            var tab = line.IndexOf((byte)'\t');
            if (tab <= 0 || tab == line.Length - 1 ||
                !ObjectId.TryParseHex(line[..tab], out var objectId))
            {
                throw new InvalidDataException("Git ls-remote output contains an invalid advertised ref.");
            }

            var referenceName = RefName.FromBytes(line[(tab + 1)..]);
            if (!referenceName.GetBytes().StartsWith("refs/"u8) || refs.ContainsKey(referenceName))
            {
                throw new InvalidDataException("Git ls-remote output contains an invalid or duplicate ref name.");
            }

            if (refs.Count == MaximumAdvertisedRefs)
            {
                throw new InvalidDataException("Git advertised more remote refs than the supported limit.");
            }

            refs.Add(referenceName, objectId!);
            output = output[(terminator + 1)..];
        }

        return refs.ToImmutable();
    }

    private static ImmutableArray<RemoteUrl> ParseEffectiveUrls(ReadOnlySpan<byte> output)
    {
        var urls = ImmutableArray.CreateBuilder<RemoteUrl>();
        while (!output.IsEmpty)
        {
            var terminator = output.IndexOf((byte)'\n');
            if (terminator < 0)
            {
                throw new InvalidDataException("Git effective push URL output ended before a line terminator.");
            }

            var value = output[..terminator];
            if (!value.IsEmpty && value[^1] == (byte)'\r')
            {
                value = value[..^1];
            }

            urls.Add(RemoteUrl.FromBytes(value));
            output = output[(terminator + 1)..];
        }

        return urls.ToImmutable();
    }

    private static ImmutableArray<RefName> ParseReferenceList(
        ReadOnlySpan<byte> output,
        ReadOnlySpan<byte> requiredPrefix,
        string subject)
    {
        var references = ImmutableArray.CreateBuilder<RefName>();
        while (!output.IsEmpty)
        {
            var terminator = output.IndexOf((byte)'\n');
            if (terminator < 0)
            {
                throw new InvalidDataException($"Git {subject} output ended before a line terminator.");
            }

            var record = output[..terminator];
            if (record.IsEmpty || record[^1] != 0 || record[..^1].Contains((byte)0))
            {
                throw new InvalidDataException($"Git {subject} output contains an invalid record.");
            }

            var referenceName = RefName.FromBytes(record[..^1]);
            if (!referenceName.GetBytes().StartsWith(requiredPrefix) ||
                referenceName.GetBytes().Length == requiredPrefix.Length ||
                (references.Count > 0 && references[^1].CompareTo(referenceName) >= 0))
            {
                throw new InvalidDataException(
                    $"Git {subject} output contains an invalid, duplicate, or unordered ref.");
            }

            if (references.Count == MaximumAdvertisedRefs)
            {
                throw new InvalidDataException($"Git returned more {subject} refs than the supported limit.");
            }

            references.Add(referenceName);
            output = output[(terminator + 1)..];
        }

        return references.ToImmutable();
    }

    private static ObjectId ParseSingleObjectId(ReadOnlySpan<byte> output, string subject)
    {
        var line = TrimSingleLine(output, subject);
        if (!ObjectId.TryParseHex(line, out var objectId))
        {
            throw new InvalidDataException($"Git returned an invalid exact {subject} object identifier.");
        }

        return objectId!;
    }

    private static ReadOnlySpan<byte> TrimSingleLine(ReadOnlySpan<byte> output, string subject)
    {
        if (output.IsEmpty || output[^1] != (byte)'\n')
        {
            throw new InvalidDataException($"Git returned an unterminated {subject} response.");
        }

        output = output[..^1];
        if (!output.IsEmpty && output[^1] == (byte)'\r')
        {
            output = output[..^1];
        }

        if (output.Contains((byte)'\n') || output.Contains((byte)'\r'))
        {
            throw new InvalidDataException($"Git returned multiple {subject} records.");
        }

        return output;
    }

    private static void RequirePlannedSourceObjectIds(
        PushPlan plan,
        ImmutableDictionary<RefName, ObjectId> live)
    {
        foreach (var update in plan.Updates)
        {
            if (update.RefSpec.Source is null)
            {
                continue;
            }

            if (!live.TryGetValue(update.RefSpec.Source, out var objectId) ||
                !objectId.Equals(update.SourceObjectId))
            {
                throw new RepositoryPreconditionException(
                    "A planned source ref changed after confirmation; review a new push plan.");
            }
        }
    }

    private static void RequirePlannedAdvertisements(
        PushPlan plan,
        ImmutableArray<(RemoteUrl Url, ImmutableDictionary<RefName, ObjectId> Refs)> advertisements)
    {
        if (advertisements.Length != plan.Updates[0].Destinations.Length)
        {
            throw new RepositoryPreconditionException(
                "The configured push destination count changed after confirmation.");
        }

        for (var destinationIndex = 0; destinationIndex < advertisements.Length; destinationIndex++)
        {
            foreach (var update in plan.Updates)
            {
                _ = advertisements[destinationIndex].Refs.TryGetValue(
                    update.RefSpec.Destination,
                    out var liveObjectId);
                var expected = update.Destinations[destinationIndex];
                if (!expected.Url.Equals(advertisements[destinationIndex].Url) ||
                    !ObjectIdsEqual(expected.ExpectedObjectId, liveObjectId))
                {
                    throw new RepositoryPreconditionException(
                        "A push destination changed after confirmation; review the new advertised OID before pushing.");
                }
            }
        }
    }

    private static void RequireSuccessfulResult(
        PushPlan plan,
        ImmutableArray<(RemoteUrl Url, ImmutableDictionary<RefName, ObjectId> Refs)> advertisements)
    {
        for (var destinationIndex = 0; destinationIndex < advertisements.Length; destinationIndex++)
        {
            foreach (var update in plan.Updates)
            {
                _ = advertisements[destinationIndex].Refs.TryGetValue(
                    update.RefSpec.Destination,
                    out var finalObjectId);
                if (update.SourceObjectId is null)
                {
                    if (finalObjectId is not null)
                    {
                        throw new PushOperationException(
                            "Git reported success, but a deleted destination still exists during verification.");
                    }
                }
                else if (!update.SourceObjectId.Equals(finalObjectId))
                {
                    throw new PushOperationException(
                        "Git reported success, but a destination OID does not match the confirmed source during verification.");
                }
            }
        }
    }

    private static bool SourceObjectIdsMatch(
        ImmutableDictionary<RefName, ObjectId> first,
        ImmutableDictionary<RefName, ObjectId> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        foreach (var pair in first)
        {
            if (!second.TryGetValue(pair.Key, out var objectId) || !pair.Value.Equals(objectId))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AdvertisementsMatch(
        ImmutableArray<(RemoteUrl Url, ImmutableDictionary<RefName, ObjectId> Refs)> first,
        ImmutableArray<(RemoteUrl Url, ImmutableDictionary<RefName, ObjectId> Refs)> second)
    {
        if (first.Length != second.Length)
        {
            return false;
        }

        for (var index = 0; index < first.Length; index++)
        {
            if (!first[index].Url.Equals(second[index].Url) ||
                !SourceObjectIdsMatch(first[index].Refs, second[index].Refs))
            {
                return false;
            }
        }

        return true;
    }

    private static ImmutableArray<RemoteUrl> GetPlannedEffectiveUrls(PushPlan plan)
        => [.. plan.Updates[0].Destinations.Select(static destination => destination.Url)];

    private static bool UrlsMatch(
        ImmutableArray<RemoteUrl> first,
        ImmutableArray<RemoteUrl> second)
        => first.AsSpan().SequenceEqual(second.AsSpan());

    private static bool ObjectIdsEqual(ObjectId? first, ObjectId? second)
        => first is null ? second is null : first.Equals(second);

    private static void AppendBounded(
        ArrayBufferWriter<byte> destination,
        ReadOnlySpan<byte> bytes,
        int maximumBytes,
        string channel)
    {
        if (destination.WrittenCount > maximumBytes - bytes.Length)
        {
            throw new InvalidDataException(
                $"Combined push {channel} exceeded the supported bounded size.");
        }

        destination.Write(bytes);
    }

    private static void RequirePushUrls(RemoteInfo remote)
    {
        if (remote.PushUrls.IsEmpty)
        {
            throw new PushOperationException("The selected remote has no configured push URL.");
        }
    }

    private static void RequireReferenceNamespace(
        RefName referenceName,
        ReadOnlySpan<byte> requiredPrefix,
        string subject)
    {
        var bytes = referenceName.GetBytes();
        if (!bytes.StartsWith(requiredPrefix) || bytes.Length == requiredPrefix.Length)
        {
            throw new ArgumentException(
                $"The selected {subject} is outside its required reference namespace.",
                nameof(referenceName));
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

    private static GitCommandException CreateCommandException(
        ProcessResult result,
        string fallbackError,
        IReadOnlyList<RemoteUrl> sensitiveUrls)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        if (string.IsNullOrEmpty(error))
        {
            error = Encoding.UTF8.GetString(result.StandardOutput.Span).Trim();
        }

        foreach (var url in sensitiveUrls)
        {
            error = url.RedactFrom(error);
        }

        return new GitCommandException(
            result.ExitCode,
            string.IsNullOrEmpty(error) ? fallbackError : error);
    }
}
