using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Ui;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies exact command hashing, fail-closed grants, serialized review, and revocation.
/// </summary>
[TestClass]
public sealed class ExecutableConfigurationBrokerTests
{
    private const string RepositoryId =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>
    /// Verifies command, source, and exposed-data changes produce different grant identities.
    /// </summary>
    [TestMethod]
    public void CommandHash_WithExecutionInputChange_InvalidatesPriorIdentity()
    {
        var original = CreateRequest(
            "printf review",
            GitConfigurationScope.Local,
            ["focused path"]);
        var same = CreateRequest(
            "printf review",
            GitConfigurationScope.Local,
            ["focused path"]);
        var changedCommand = CreateRequest(
            "printf changed",
            GitConfigurationScope.Local,
            ["focused path"]);
        var changedScope = CreateRequest(
            "printf review",
            GitConfigurationScope.Global,
            ["focused path"]);
        var changedExposure = CreateRequest(
            "printf review",
            GitConfigurationScope.Local,
            ["focused path", "selected paths"]);

        Assert.AreEqual(original.CommandHash, same.CommandHash);
        Assert.AreNotEqual(original.CommandHash, changedCommand.CommandHash);
        Assert.AreNotEqual(original.CommandHash, changedScope.CommandHash);
        Assert.AreNotEqual(original.CommandHash, changedExposure.CommandHash);
        Assert.AreEqual(64, original.CommandHash.Length);
    }

    /// <summary>
    /// Verifies only a valid user-global grant can bypass review and reload revokes it immediately.
    /// </summary>
    [TestMethod]
    public async Task AuthorizeAsync_WithRepositoryGrant_PersistsGlobalOnlyAndHonorsRevocation()
    {
        var request = CreateRequest(
            "printf review",
            GitConfigurationScope.Local,
            ["focused path"]);
        var localGrant = CreateGrantValue(request.CommandHash);
        var configuration = CreateSnapshot(GitConfigurationScope.Local, localGrant);
        var persistenceCount = 0;
        var store = new ExecutableCapabilityGrantStore(
            RepositoryId,
            configuration,
            (key, value, _) =>
            {
                persistenceCount++;
                return Task.FromResult(CreateSnapshot(GitConfigurationScope.Global, value));
            });
        using var responder = new ExecutableCapabilityCoordinator();
        var broker = new ExecutableConfigurationBroker(store, responder);

        var authorization = broker.AuthorizeAsync(
            request,
            TestContext.Current!.CancellationToken);
        var prompt = await WaitForPromptAsync(responder, TestContext.Current.CancellationToken);
        Assert.IsTrue(responder.Decide(
            prompt.Id,
            ExecutableCapabilityDecision.AllowRepository));

        Assert.IsTrue(await authorization);
        Assert.AreEqual(1, persistenceCount);
        Assert.IsTrue(store.IsGranted(request));
        Assert.IsNull(store.LoadError);

        Assert.IsTrue(await broker.AuthorizeAsync(
            request,
            TestContext.Current.CancellationToken));
        Assert.IsNull(responder.Current);
        Assert.AreEqual(1, persistenceCount);

        store.Reload(new GitConfigurationSnapshot([]));
        var afterRevocation = broker.AuthorizeAsync(
            request,
            TestContext.Current.CancellationToken);
        var revokedPrompt = await WaitForPromptAsync(
            responder,
            TestContext.Current.CancellationToken);
        Assert.IsTrue(responder.Cancel(revokedPrompt.Id));
        Assert.IsFalse(await afterRevocation);
    }

    /// <summary>
    /// Verifies malformed persistent data fails closed without preventing a one-time review.
    /// </summary>
    [TestMethod]
    public async Task AuthorizeAsync_WithMalformedGrant_FailsClosedAndAllowsExplicitOnce()
    {
        var request = CreateRequest(
            "printf review",
            GitConfigurationScope.Local,
            ["focused path"]);
        var configuration = CreateSnapshot(
            GitConfigurationScope.Global,
            GitConfigurationValue.FromBytes("{\"version\":1,\"commands\":[\"bad\"]}"u8));
        var store = new ExecutableCapabilityGrantStore(
            RepositoryId,
            configuration,
            static (_, _, _) => throw new InvalidOperationException(
                "A one-time decision must not persist configuration."));
        using var responder = new ExecutableCapabilityCoordinator();
        var broker = new ExecutableConfigurationBroker(store, responder);

        Assert.IsNotNull(store.LoadError);
        Assert.IsFalse(store.IsGranted(request));
        var authorization = broker.AuthorizeAsync(
            request,
            TestContext.Current!.CancellationToken);
        var prompt = await WaitForPromptAsync(responder, TestContext.Current.CancellationToken);
        Assert.IsTrue(responder.Decide(
            prompt.Id,
            ExecutableCapabilityDecision.AllowOnce));

        Assert.IsTrue(await authorization);
        Assert.IsFalse(store.IsGranted(request));
    }

    private static ExecutableCapabilityRequest CreateRequest(
        string command,
        GitConfigurationScope scope,
        ImmutableArray<string> exposedData)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        };
        var shell = new ExecutableResolver(
            new TestProcessEnvironment(variables)).Resolve(ProgramKind.Shell);
        return new ExecutableCapabilityRequest(
            GitConfigurationExecutionKind.Tool,
            "guitool.review.cmd",
            scope,
            GitConfigurationOrigin.FromBytes("file:.git/config"u8),
            command,
            shell,
            CanonicalDirectory.Create(Path.GetTempPath()),
            usesShell: true,
            exposedData);
    }

    private static GitConfigurationSnapshot CreateSnapshot(
        GitConfigurationScope scope,
        GitConfigurationValue value)
    {
        var key = GitConfigurationKey.FromBytes(
            Encoding.UTF8.GetBytes($"gitsail.trustedrepository.{RepositoryId}"));
        return new GitConfigurationSnapshot(
        [
            new GitConfigurationEntry(
                scope,
                GitConfigurationOrigin.FromBytes("file:test-config"u8),
                key,
                value),
        ]);
    }

    private static GitConfigurationValue CreateGrantValue(string commandHash)
        => GitConfigurationValue.FromBytes(
            Encoding.UTF8.GetBytes($"{{\"version\":1,\"commands\":[\"{commandHash}\"]}}"));

    private static async Task<ExecutableCapabilityPrompt> WaitForPromptAsync(
        ExecutableCapabilityCoordinator responder,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (responder.Current is { } prompt)
            {
                return prompt;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        Assert.Fail("The executable capability prompt did not become current.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }
}
