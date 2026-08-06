using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies exact Git-resolved push planning, leasing, execution, and stale-state rejection.
/// </summary>
[TestClass]
public sealed class PushServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private RepositoryMutationCoordinator? _coordinator;
    private GitChildEnvironmentFactory? _environmentFactory;

    /// <summary>
    /// Creates an isolated home and resolves Git for each push-service test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-push-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        _coordinator = new RepositoryMutationCoordinator();
        _environmentFactory = TestProcessEnvironment.CreateGitFactory(_temporaryDirectory);
        _installation = await new GitVersionService(
            new ExecutableResolver(new RuntimeProcessEnvironment()),
            _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
    }

    /// <summary>
    /// Removes isolated repositories and the mutation coordinator after each test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        _coordinator?.Dispose();
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            TestDirectory.Delete(_temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies a new current branch is planned exactly, leased as absent, pushed, and tracked.
    /// </summary>
    [TestMethod]
    public async Task PrepareAndPushAsync_WithNewCurrentBranch_PushesExactOidAndSetsUpstream()
    {
        var setup = await CreateRemoteRepositoryAsync("new-branch");
        await RunGitAsync(setup.RepositoryPath, "config", "push.default", "current");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);
        var origin = catalog.Remotes.Single();

        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            origin,
            GitOptionOverride.Configured,
            TestContext.Current!.CancellationToken);

        Assert.HasCount(1, plan.Updates);
        var update = plan.Updates[0];
        Assert.AreEqual("refs/heads/main:refs/heads/main", update.RefSpec.ToString());
        Assert.AreEqual(PushRelationship.New, update.Destinations[0].Relationship);
        Assert.IsNull(update.Destinations[0].ExpectedObjectId);
        Assert.AreEqual(1, update.Destinations[0].CommitCount);
        Assert.IsNull(plan.UpstreamName);
        _ = await service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.Normal, setUpstream: true, GitOptionOverride.Configured),
            TestContext.Current.CancellationToken);

        var remoteOid = (await RunGitForOutputAsync(
            setup.RemotePath,
            "rev-parse",
            "--verify",
            "refs/heads/main")).Trim();
        Assert.AreEqual(update.SourceObjectId?.ToString(), remoteOid);
        Assert.AreEqual("origin", (await RunGitForOutputAsync(
            setup.RepositoryPath,
            "config",
            "--get",
            "branch.main.remote")).Trim());
        Assert.AreEqual("refs/heads/main", (await RunGitForOutputAsync(
            setup.RepositoryPath,
            "config",
            "--get",
            "branch.main.merge")).Trim());
    }

    /// <summary>
    /// Verifies push.autoSetupRemote is preserved from Git's dry run through exact execution.
    /// </summary>
    [TestMethod]
    public async Task PrepareAndPushAsync_WithAutoSetupRemote_SetsUpstreamFromGitIntent()
    {
        var setup = await CreateRemoteRepositoryAsync("auto-upstream");
        await RunGitAsync(setup.RepositoryPath, "config", "push.default", "current");
        await RunGitAsync(setup.RepositoryPath, "config", "push.autoSetupRemote", "true");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);

        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            GitOptionOverride.Configured,
            TestContext.Current!.CancellationToken);

        Assert.IsTrue(plan.WouldSetUpstream);
        _ = await service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.Normal, setUpstream: false, GitOptionOverride.Configured),
            TestContext.Current.CancellationToken);
        Assert.AreEqual("origin", (await RunGitForOutputAsync(
            setup.RepositoryPath,
            "config",
            "--get",
            "branch.main.remote")).Trim());
        Assert.AreEqual("refs/heads/main", (await RunGitForOutputAsync(
            setup.RepositoryPath,
            "config",
            "--get",
            "branch.main.merge")).Trim());
    }

    /// <summary>
    /// Verifies normal fast-forward planning captures exact expected OID and introduced commit count.
    /// </summary>
    [TestMethod]
    public async Task PrepareAndPushAsync_WithFastForward_UsesFrozenExpectedOid()
    {
        var setup = await CreateRemoteRepositoryAsync("fast-forward");
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", "--set-upstream", "origin", "main");
        var expectedOid = (await RunGitForOutputAsync(setup.RepositoryPath, "rev-parse", "HEAD")).Trim();
        await CommitEmptyAsync(setup.RepositoryPath, "next");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);

        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            GitOptionOverride.Configured,
            TestContext.Current!.CancellationToken);

        var destination = plan.Updates.Single().Destinations.Single();
        Assert.AreEqual(expectedOid, destination.ExpectedObjectId?.ToString());
        Assert.AreEqual(PushRelationship.FastForward, destination.Relationship);
        Assert.AreEqual(1, destination.CommitCount);
        Assert.AreEqual("refs/remotes/origin/main", plan.UpstreamName?.DisplayText);
        _ = await service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.Normal, setUpstream: false, GitOptionOverride.Configured),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(
            plan.Updates.Single().SourceObjectId?.ToString(),
            (await RunGitForOutputAsync(setup.RemotePath, "rev-parse", "refs/heads/main")).Trim());
    }

    /// <summary>
    /// Verifies a non-fast-forward update requires and succeeds only through its explicit expected-OID lease.
    /// </summary>
    [TestMethod]
    public async Task PushAsync_WithNonFastForward_RequiresExplicitLease()
    {
        var setup = await CreateRemoteRepositoryAsync("rewrite");
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", "--set-upstream", "origin", "main");
        var baseline = (await RunGitForOutputAsync(setup.RepositoryPath, "rev-parse", "HEAD")).Trim();
        await CommitEmptyAsync(setup.RepositoryPath, "published");
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", "origin", "main");
        var published = (await RunGitForOutputAsync(setup.RepositoryPath, "rev-parse", "HEAD")).Trim();
        await RunGitAsync(setup.RepositoryPath, "reset", "--hard", "--quiet", baseline);
        await CommitEmptyAsync(setup.RepositoryPath, "replacement");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);
        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            GitOptionOverride.Configured,
            TestContext.Current!.CancellationToken);

        Assert.IsTrue(plan.RequiresForce);
        Assert.AreEqual(PushRelationship.NonFastForward, plan.Updates[0].Destinations[0].Relationship);
        Assert.AreEqual(published, plan.Updates[0].Destinations[0].ExpectedObjectId?.ToString());
        _ = await Assert.ThrowsExactlyAsync<PushOperationException>(() => service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.Normal, setUpstream: false, GitOptionOverride.Configured),
            TestContext.Current.CancellationToken));
        Assert.AreEqual(
            published,
            (await RunGitForOutputAsync(setup.RemotePath, "rev-parse", "refs/heads/main")).Trim());

        _ = await service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.ExplicitLease, setUpstream: false, GitOptionOverride.Configured),
            TestContext.Current.CancellationToken);
        Assert.AreEqual(
            plan.Updates[0].SourceObjectId?.ToString(),
            (await RunGitForOutputAsync(setup.RemotePath, "rev-parse", "refs/heads/main")).Trim());
    }

    /// <summary>
    /// Verifies a remote update after confirmation rejects the frozen plan before any source is pushed.
    /// </summary>
    [TestMethod]
    public async Task PushAsync_AfterRemoteChanged_RejectsStaleExpectedOid()
    {
        var setup = await CreateRemoteRepositoryAsync("stale");
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", "--set-upstream", "origin", "main");
        var baseline = (await RunGitForOutputAsync(setup.RepositoryPath, "rev-parse", "HEAD")).Trim();
        await CommitEmptyAsync(setup.RepositoryPath, "planned");
        var plannedSource = (await RunGitForOutputAsync(setup.RepositoryPath, "rev-parse", "HEAD")).Trim();
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);
        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            GitOptionOverride.Configured,
            TestContext.Current!.CancellationToken);
        await RunGitAsync(setup.RepositoryPath, "branch", "intruder", baseline);
        await RunGitAsync(setup.RepositoryPath, "switch", "--quiet", "intruder");
        await CommitEmptyAsync(setup.RepositoryPath, "intruder");
        var intruder = (await RunGitForOutputAsync(setup.RepositoryPath, "rev-parse", "HEAD")).Trim();
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", "origin", "intruder:main");
        await RunGitAsync(setup.RepositoryPath, "switch", "--quiet", "main");

        _ = await Assert.ThrowsExactlyAsync<RepositoryPreconditionException>(() => service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.Normal, setUpstream: false, GitOptionOverride.Configured),
            TestContext.Current.CancellationToken));

        Assert.AreEqual(
            intruder,
            (await RunGitForOutputAsync(setup.RemotePath, "rev-parse", "refs/heads/main")).Trim());
        Assert.AreNotEqual(plannedSource, intruder);
    }

    /// <summary>
    /// Verifies each push URL receives the lease for its own independently advertised destination OID.
    /// </summary>
    [TestMethod]
    public async Task PrepareAndPushAsync_WithDifferentPushUrlOids_UsesIndependentExactLeases()
    {
        var setup = await CreateRemoteRepositoryAsync("multiple-urls");
        var secondRemotePath = Path.Combine(_temporaryDirectory!, "multiple-urls-second.git");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--bare", "--", secondRemotePath);
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", setup.RemotePath, "main");
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", secondRemotePath, "main");
        var firstExpected = (await RunGitForOutputAsync(setup.RepositoryPath, "rev-parse", "HEAD")).Trim();
        await CommitEmptyAsync(setup.RepositoryPath, "second destination baseline");
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", secondRemotePath, "main");
        var secondExpected = (await RunGitForOutputAsync(setup.RepositoryPath, "rev-parse", "HEAD")).Trim();
        await CommitEmptyAsync(setup.RepositoryPath, "planned source");
        var plannedSource = (await RunGitForOutputAsync(setup.RepositoryPath, "rev-parse", "HEAD")).Trim();
        await RunGitAsync(setup.RepositoryPath, "config", "push.default", "current");
        await RunGitAsync(
            setup.RepositoryPath,
            "config",
            "--add",
            "remote.origin.pushurl",
            setup.RemotePath);
        await RunGitAsync(
            setup.RepositoryPath,
            "config",
            "--add",
            "remote.origin.pushurl",
            secondRemotePath);
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);

        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            GitOptionOverride.Configured,
            TestContext.Current!.CancellationToken);

        Assert.HasCount(2, plan.Updates.Single().Destinations);
        Assert.AreEqual(
            firstExpected,
            plan.Updates[0].Destinations[0].ExpectedObjectId?.ToString());
        Assert.AreEqual(2, plan.Updates[0].Destinations[0].CommitCount);
        Assert.AreEqual(
            secondExpected,
            plan.Updates[0].Destinations[1].ExpectedObjectId?.ToString());
        Assert.AreEqual(1, plan.Updates[0].Destinations[1].CommitCount);

        _ = await service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.Normal, setUpstream: false, GitOptionOverride.Configured),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(
            plannedSource,
            (await RunGitForOutputAsync(setup.RemotePath, "rev-parse", "refs/heads/main")).Trim());
        Assert.AreEqual(
            plannedSource,
            (await RunGitForOutputAsync(secondRemotePath, "rev-parse", "refs/heads/main")).Trim());
    }

    /// <summary>
    /// Verifies an upstream request remains attached to the selected remote after an exact multi-URL push.
    /// </summary>
    [TestMethod]
    public async Task PrepareAndPushAsync_WithMultiplePushUrls_SetsSelectedRemoteAsUpstream()
    {
        var setup = await CreateRemoteRepositoryAsync("multiple-upstream");
        var secondRemotePath = Path.Combine(_temporaryDirectory!, "multiple-upstream-second.git");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--bare", "--", secondRemotePath);
        await RunGitAsync(setup.RepositoryPath, "config", "push.default", "current");
        await RunGitAsync(
            setup.RepositoryPath,
            "config",
            "--add",
            "remote.origin.pushurl",
            setup.RemotePath);
        await RunGitAsync(
            setup.RepositoryPath,
            "config",
            "--add",
            "remote.origin.pushurl",
            secondRemotePath);
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);
        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            GitOptionOverride.Configured,
            TestContext.Current!.CancellationToken);

        _ = await service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.Normal, setUpstream: true, GitOptionOverride.Configured),
            TestContext.Current.CancellationToken);

        Assert.AreEqual("origin", (await RunGitForOutputAsync(
            setup.RepositoryPath,
            "config",
            "--get",
            "branch.main.remote")).Trim());
        Assert.AreEqual("refs/heads/main", (await RunGitForOutputAsync(
            setup.RepositoryPath,
            "config",
            "--get",
            "branch.main.merge")).Trim());
        Assert.AreEqual(
            plan.Updates.Single().SourceObjectId?.ToString(),
            (await RunGitForOutputAsync(setup.RemotePath, "rev-parse", "refs/heads/main")).Trim());
        Assert.AreEqual(
            plan.Updates.Single().SourceObjectId?.ToString(),
            (await RunGitForOutputAsync(secondRemotePath, "rev-parse", "refs/heads/main")).Trim());
    }

    /// <summary>
    /// Verifies a mirror remote executes only the exact refs frozen into its reviewed plan.
    /// </summary>
    [TestMethod]
    public async Task PrepareAndPushAsync_WithMirrorRemote_ExecutesFrozenRefSpecs()
    {
        var setup = await CreateRemoteRepositoryAsync("mirror");
        await RunGitAsync(setup.RepositoryPath, "config", "remote.origin.mirror", "true");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);
        var plan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            GitOptionOverride.Configured,
            TestContext.Current!.CancellationToken);

        Assert.IsTrue(plan.Updates.Any(static update =>
            update.RefSpec.ToString() == "refs/heads/main:refs/heads/main"));
        _ = await service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.Normal, setUpstream: false, GitOptionOverride.Configured),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(
            plan.Updates.Single(static update =>
                update.RefSpec.ToString() == "refs/heads/main:refs/heads/main").SourceObjectId?.ToString(),
            (await RunGitForOutputAsync(setup.RemotePath, "rev-parse", "refs/heads/main")).Trim());
    }

    /// <summary>
    /// Verifies every documented push.default mode is delegated to Git and resolved without same-tail ambiguity.
    /// </summary>
    [TestMethod]
    public async Task PrepareAsync_AcrossPushDefaultModes_UsesCompleteGitSemantics()
    {
        var setup = await CreateRemoteRepositoryAsync("defaults");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);
        foreach (var mode in new[] { "nothing", "simple", "upstream", "tracking", "matching" })
        {
            await RunGitAsync(setup.RepositoryPath, "config", "push.default", mode);
            _ = await Assert.ThrowsExactlyAsync<GitCommandException>(() => service.PrepareAsync(
                workingDirectory,
                catalog,
                catalog.Remotes.Single(),
                GitOptionOverride.Configured,
                TestContext.Current!.CancellationToken));
        }

        await RunGitAsync(setup.RepositoryPath, "config", "push.default", "current");
        var currentPlan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            GitOptionOverride.Configured,
            TestContext.Current!.CancellationToken);
        Assert.AreEqual("refs/heads/main:refs/heads/main", currentPlan.Updates.Single().RefSpec.ToString());
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", "--set-upstream", "origin", "main");
        await RunGitAsync(setup.RepositoryPath, "tag", "main");
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", "origin", "refs/tags/main:refs/tags/main");

        foreach (var mode in new[] { "simple", "current", "upstream", "tracking", "matching" })
        {
            await RunGitAsync(setup.RepositoryPath, "config", "push.default", mode);
            var plan = await service.PrepareAsync(
                workingDirectory,
                catalog,
                catalog.Remotes.Single(),
                GitOptionOverride.Configured,
                TestContext.Current.CancellationToken);
            Assert.HasCount(1, plan.Updates);
            Assert.AreEqual("refs/heads/main:refs/heads/main", plan.Updates[0].RefSpec.ToString());
        }

        await RunGitAsync(setup.RepositoryPath, "switch", "--quiet", "--create", "topic");
        await CommitEmptyAsync(setup.RepositoryPath, "topic baseline");
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", "origin", "topic");
        await CommitEmptyAsync(setup.RepositoryPath, "topic update");
        await RunGitAsync(setup.RepositoryPath, "switch", "--quiet", "main");
        await CommitEmptyAsync(setup.RepositoryPath, "main update");
        await RunGitAsync(setup.RepositoryPath, "config", "push.default", "matching");

        var matchingPlan = await service.PrepareAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            GitOptionOverride.Configured,
            TestContext.Current.CancellationToken);

        Assert.HasCount(2, matchingPlan.Updates);
        Assert.IsTrue(matchingPlan.Updates.Any(static update =>
            update.RefSpec.ToString() == "refs/heads/main:refs/heads/main"));
        Assert.IsTrue(matchingPlan.Updates.Any(static update =>
            update.RefSpec.ToString() == "refs/heads/topic:refs/heads/topic"));
    }

    /// <summary>
    /// Verifies pushInsteadOf expansion binds planning to the effective destination Git will contact.
    /// </summary>
    [TestMethod]
    public async Task PrepareAsync_WithPushUrlRewrite_UsesEffectiveDestination()
    {
        var setup = await CreateRemoteRepositoryAsync("rewrite-url");
        await RunGitAsync(setup.RepositoryPath, "remote", "set-url", "origin", "alias:");
        await RunGitAsync(
            setup.RepositoryPath,
            "config",
            $"url.{setup.RemotePath}.pushInsteadOf",
            "alias:");
        await RunGitAsync(setup.RepositoryPath, "config", "push.default", "current");
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);
        Assert.AreEqual("alias:", catalog.Remotes.Single().PushUrls.Single().RedactedDisplayText);

        var plan = await CreateService().PrepareAsync(
            CanonicalDirectory.Create(setup.RepositoryPath),
            catalog,
            catalog.Remotes.Single(),
            GitOptionOverride.Configured,
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(
            RemoteUrl.FromText(setup.RemotePath).RedactedDisplayText,
            plan.Updates.Single().Destinations.Single().Url.RedactedDisplayText);
        Assert.AreEqual(PushRelationship.New, plan.Updates[0].Destinations[0].Relationship);
    }

    /// <summary>
    /// Verifies stable local tag selection and an exact annotated-tag object push through the shared plan.
    /// </summary>
    [TestMethod]
    public async Task CaptureAndPrepareTagAsync_WithAnnotatedTag_PushesExactTagObject()
    {
        var setup = await CreateRemoteRepositoryAsync("tag-push");
        await RunGitAsync(
            setup.RepositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "tag",
            "--annotate",
            "--message",
            "release tag",
            "release/v1");
        await RunGitAsync(setup.RepositoryPath, "tag", "lightweight");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);

        var tags = await service.CaptureLocalTagsAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken);
        Assert.HasCount(2, tags);
        Assert.AreEqual("refs/tags/lightweight", tags[0].DisplayText);
        Assert.AreEqual("refs/tags/release/v1", tags[1].DisplayText);

        var plan = await service.PrepareTagAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            tags[1],
            TestContext.Current.CancellationToken);

        Assert.AreEqual("refs/tags/release/v1:refs/tags/release/v1", plan.Updates[0].RefSpec.ToString());
        Assert.AreEqual(PushRelationship.New, plan.Updates[0].Destinations[0].Relationship);
        _ = await service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.Normal, setUpstream: false, GitOptionOverride.Disabled),
            TestContext.Current.CancellationToken);
        Assert.AreEqual(
            (await RunGitForOutputAsync(setup.RepositoryPath, "rev-parse", "refs/tags/release/v1")).Trim(),
            (await RunGitForOutputAsync(setup.RemotePath, "rev-parse", "refs/tags/release/v1")).Trim());
    }

    /// <summary>
    /// Verifies replacing an existing tag requires its exact advertised object lease.
    /// </summary>
    [TestMethod]
    public async Task PrepareTagAsync_WithReplacement_RequiresExplicitLease()
    {
        var setup = await CreateRemoteRepositoryAsync("tag-replacement");
        await RunGitAsync(setup.RepositoryPath, "tag", "release");
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", "origin", "refs/tags/release");
        var publishedTag = (await RunGitForOutputAsync(
            setup.RepositoryPath,
            "rev-parse",
            "refs/tags/release")).Trim();
        await CommitEmptyAsync(setup.RepositoryPath, "replacement target");
        await RunGitAsync(setup.RepositoryPath, "tag", "--force", "release");
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);
        var tag = (await service.CaptureLocalTagsAsync(
            workingDirectory,
            TestContext.Current!.CancellationToken)).Single();

        var plan = await service.PrepareTagAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            tag,
            TestContext.Current.CancellationToken);

        Assert.IsTrue(plan.RequiresForce);
        Assert.AreEqual(
            publishedTag,
            plan.Updates[0].Destinations[0].ExpectedObjectId?.ToString());
        _ = await Assert.ThrowsExactlyAsync<PushOperationException>(() => service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.Normal, setUpstream: false, GitOptionOverride.Disabled),
            TestContext.Current.CancellationToken));
        _ = await service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.ExplicitLease, setUpstream: false, GitOptionOverride.Disabled),
            TestContext.Current.CancellationToken);
        Assert.AreEqual(
            plan.Updates[0].SourceObjectId?.ToString(),
            (await RunGitForOutputAsync(setup.RemotePath, "rev-parse", "refs/tags/release")).Trim());
    }

    /// <summary>
    /// Verifies advertised branch selection and exact leased deletion across inconsistent destinations.
    /// </summary>
    [TestMethod]
    public async Task CaptureAndPrepareRemoteBranchDeletionAsync_WithMultipleUrls_DeletesWherePresent()
    {
        var setup = await CreateRemoteRepositoryAsync("branch-deletion");
        var secondRemotePath = Path.Combine(_temporaryDirectory!, "branch-deletion-second.git");
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--bare", "--", secondRemotePath);
        await RunGitAsync(setup.RepositoryPath, "branch", "feature");
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", setup.RemotePath, "main", "feature");
        await RunGitAsync(setup.RepositoryPath, "push", "--quiet", secondRemotePath, "main");
        await RunGitAsync(
            setup.RepositoryPath,
            "config",
            "--add",
            "remote.origin.pushurl",
            setup.RemotePath);
        await RunGitAsync(
            setup.RepositoryPath,
            "config",
            "--add",
            "remote.origin.pushurl",
            secondRemotePath);
        var service = CreateService();
        var workingDirectory = CanonicalDirectory.Create(setup.RepositoryPath);
        var catalog = await CaptureCatalogAsync(setup.RepositoryPath);

        var branches = await service.CaptureRemoteBranchesAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            TestContext.Current!.CancellationToken);
        Assert.HasCount(2, branches);
        var feature = branches.Single(static branch => branch.DisplayText == "refs/heads/feature");

        var plan = await service.PrepareRemoteBranchDeletionAsync(
            workingDirectory,
            catalog,
            catalog.Remotes.Single(),
            feature,
            TestContext.Current.CancellationToken);

        Assert.IsTrue(plan.IncludesDeletion);
        Assert.IsNotNull(plan.Updates[0].Destinations[0].ExpectedObjectId);
        Assert.IsNull(plan.Updates[0].Destinations[1].ExpectedObjectId);
        _ = await Assert.ThrowsExactlyAsync<PushOperationException>(() => service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.Normal, setUpstream: false, GitOptionOverride.Disabled),
            TestContext.Current.CancellationToken));
        _ = await service.PushAsync(
            workingDirectory,
            plan,
            new PushOptions(PushSafetyMode.ExplicitLease, setUpstream: false, GitOptionOverride.Disabled),
            TestContext.Current.CancellationToken);

        var missing = await RunGitAsync(
            setup.RemotePath,
            ["rev-parse", "--verify", "--quiet", "refs/heads/feature"],
            expectSuccess: false);
        Assert.AreEqual(1, missing.ExitCode);
    }

    private PushService CreateService()
    {
        var credentialPromptBroker = new CredentialPromptBroker(new TestCredentialPromptResponder());
        var remoteService = new RemoteService(
            _installation!,
            _runner!,
            _environmentFactory!,
            _coordinator!,
            credentialPromptBroker);
        return new PushService(
            _installation!,
            _runner!,
            _environmentFactory!,
            _coordinator!,
            remoteService,
            credentialPromptBroker);
    }

    private async Task<RemoteCatalog> CaptureCatalogAsync(string repositoryPath)
    {
        var credentialPromptBroker = new CredentialPromptBroker(new TestCredentialPromptResponder());
        var service = new RemoteService(
            _installation!,
            _runner!,
            _environmentFactory!,
            _coordinator!,
            credentialPromptBroker);
        return await service.CaptureAsync(
            CanonicalDirectory.Create(repositoryPath),
            TestContext.Current!.CancellationToken);
    }

    private async Task<(string RepositoryPath, string RemotePath)> CreateRemoteRepositoryAsync(string name)
    {
        var repositoryPath = Path.Combine(_temporaryDirectory!, name);
        var remotePath = Path.Combine(_temporaryDirectory!, $"{name}.git");
        await RunGitAsync(
            _temporaryDirectory!,
            "init",
            "--quiet",
            "--initial-branch=main",
            "--",
            repositoryPath);
        await RunGitAsync(_temporaryDirectory!, "init", "--quiet", "--bare", "--", remotePath);
        await CommitEmptyAsync(repositoryPath, "baseline");
        await RunGitAsync(repositoryPath, "remote", "add", "origin", remotePath);
        return (repositoryPath, remotePath);
    }

    private Task<ProcessResult> CommitEmptyAsync(string repositoryPath, string message)
        => RunGitAsync(
            repositoryPath,
            "-c",
            "user.name=GitSail Tests",
            "-c",
            "user.email=gitsail@example.invalid",
            "commit",
            "--quiet",
            "--allow-empty",
            "--no-gpg-sign",
            "--message",
            message);

    private async Task<string> RunGitForOutputAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunGitAsync(workingDirectory, arguments, expectSuccess: true);
        return Encoding.UTF8.GetString(result.StandardOutput.Span);
    }

    private Task<ProcessResult> RunGitAsync(string workingDirectory, params string[] arguments)
        => RunGitAsync(workingDirectory, arguments, expectSuccess: true);

    private async Task<ProcessResult> RunGitAsync(
        string workingDirectory,
        string[] arguments,
        bool expectSuccess)
    {
        var invocation = new ProcessInvocation(
            _installation!.Executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(workingDirectory),
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!).CreateCheckoutEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(16 * 1024 * 1024, 16 * 1024 * 1024));
        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);
        if (expectSuccess)
        {
            Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        }

        return result;
    }
}
