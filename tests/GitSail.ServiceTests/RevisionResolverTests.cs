using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies typed revision validation against isolated real Git repositories.
/// </summary>
[TestClass]
public sealed class RevisionResolverTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;

    /// <summary>
    /// Creates an isolated committed repository for each revision-resolution test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-revision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
        _installation = await new GitVersionService(resolver, _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
        await RunGitAsync(_temporaryDirectory, "init", "--quiet", "--initial-branch=main");
        File.WriteAllText(Path.Combine(_temporaryDirectory, "tracked.txt"), "content\n");
        await RunGitAsync(_temporaryDirectory, "add", "--", "tracked.txt");
        await RunGitAsync(_temporaryDirectory, "commit", "--quiet", "--no-gpg-sign", "--message=initial");
    }

    /// <summary>
    /// Removes the isolated repository and home after each test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            TestDirectory.Delete(_temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies that HEAD resolves to the exact current commit.
    /// </summary>
    [TestMethod]
    public async Task ResolveCommitAsync_WithHead_ReturnsExactCommit()
    {
        var service = new RevisionResolver(
            _installation!,
            _runner!,
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!));

        var resolved = await service.ResolveCommitAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            Revision.Create("HEAD"),
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(RepositoryObjectFormat.Sha1, resolved.CommitObjectId.Format);
        Assert.HasCount(40, resolved.CommitObjectId.ToString());
    }

    /// <summary>
    /// Verifies that an option-looking revision is protected by the end-of-options marker.
    /// </summary>
    [TestMethod]
    public async Task ResolveCommitAsync_WithOptionLookingRevision_ReturnsGitFailure()
    {
        var service = new RevisionResolver(
            _installation!,
            _runner!,
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory!));

        var exception = await Assert.ThrowsExactlyAsync<GitCommandException>(() => service.ResolveCommitAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            Revision.Create("--help"),
            TestContext.Current!.CancellationToken));

        Assert.AreNotEqual(0, exception.ExitCode);
    }

    private async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var environment = ChildEnvironment.Create(
        [
            new KeyValuePair<string, string>("HOME", _temporaryDirectory!),
            new KeyValuePair<string, string>("USERPROFILE", _temporaryDirectory!),
            new KeyValuePair<string, string>("GIT_CONFIG_NOSYSTEM", "1"),
            new KeyValuePair<string, string>("GIT_AUTHOR_NAME", "GitSail Test"),
            new KeyValuePair<string, string>("GIT_AUTHOR_EMAIL", "gitsail@example.invalid"),
            new KeyValuePair<string, string>("GIT_COMMITTER_NAME", "GitSail Test"),
            new KeyValuePair<string, string>("GIT_COMMITTER_EMAIL", "gitsail@example.invalid"),
            new KeyValuePair<string, string>("GIT_AUTHOR_DATE", "2000-01-01T00:00:00Z"),
            new KeyValuePair<string, string>("GIT_COMMITTER_DATE", "2000-01-01T00:00:00Z"),
            new KeyValuePair<string, string>("LANG", "C"),
            new KeyValuePair<string, string>("LC_ALL", "C"),
        ]);
        var invocation = new ProcessInvocation(
            _installation!.Executable,
            [.. arguments.Select(ProcessArgument.Literal)],
            CanonicalDirectory.Create(workingDirectory),
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));

        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
    }
}
