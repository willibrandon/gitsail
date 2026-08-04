using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Testing;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies exact incremental blame and its responsive view against isolated real Git repositories.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BlameServiceTests
{
    private static readonly int[] s_expectedRangeLines = [2, 3];
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private GitChildEnvironmentFactory? _environmentFactory;
    private RepositoryLocation? _repository;
    private BlameService? _service;

    /// <summary>
    /// Creates an isolated repository with committed and dirty line history for each test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-blame-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        var processEnvironment = CreateProcessEnvironment();
        var resolver = new ExecutableResolver(processEnvironment);
        _installation = await new GitVersionService(resolver, _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
        _environmentFactory = new GitChildEnvironmentFactory(processEnvironment);
        await RunGitAsync(ProcessArgument.Literal("init"), ProcessArgument.Literal("--quiet"), ProcessArgument.Literal("--initial-branch=main"));
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "story file.txt"),
            "alpha\nbeta\ngamma\n",
            TestContext.Current!.CancellationToken);
        await RunGitAsync(
            ProcessArgument.Literal("add"),
            ProcessArgument.Literal("--"),
            ProcessArgument.Literal("story file.txt"));
        await RunGitAsync(
            ProcessArgument.Literal("commit"),
            ProcessArgument.Literal("--quiet"),
            ProcessArgument.Literal("--no-gpg-sign"),
            ProcessArgument.Literal("--message=initial story"));
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "story file.txt"),
            "alpha\nbeta revised\ngamma\n",
            TestContext.Current!.CancellationToken);
        await RunGitAsync(
            ProcessArgument.Literal("add"),
            ProcessArgument.Literal("--"),
            ProcessArgument.Literal("story file.txt"));
        await RunGitAsync(
            ProcessArgument.Literal("commit"),
            ProcessArgument.Literal("--quiet"),
            ProcessArgument.Literal("--no-gpg-sign"),
            ProcessArgument.Literal("--message=revise beta"));
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "story file.txt"),
            "alpha\nbeta revised\ngamma\nworktree only\n",
            TestContext.Current!.CancellationToken);
        _repository = await new RepositoryDiscoveryService(_installation, _runner, _environmentFactory)
            .DiscoverAsync(
                CanonicalDirectory.Create(_temporaryDirectory),
                TestContext.Current!.CancellationToken);
        _service = new BlameService(_installation, _runner, _environmentFactory);
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
    /// Verifies worktree bytes supplied through standard input match structured attribution including dirty lines.
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WithWorktreeContents_ReturnsCommittedAndDirtyLines()
    {
        var catalog = await _service!.CaptureAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            _repository!,
            new BlameRequest(
                Revision: null,
                CreatePath("story file.txt"),
                Range: null,
                DetectMoves: false,
                DetectCopies: false),
            TestContext.Current!.CancellationToken);

        Assert.IsNull(catalog.ResolvedRevision);
        Assert.AreEqual(
            "alpha\nbeta revised\ngamma\nworktree only\n",
            Encoding.UTF8.GetString(catalog.Content.Span));
        Assert.HasCount(4, catalog.Attributions);
        Assert.IsTrue(catalog.Attributions[3].Commit.IsUncommitted);
        Assert.AreEqual(4, catalog.Attributions[3].ResultLineNumber);
        Assert.AreEqual("story file.txt", catalog.Attributions[3].SourcePath.DisplayText);
    }

    /// <summary>
    /// Verifies an immutable revision and inclusive range return exact historical bytes and only requested lines.
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WithRevisionRange_ReturnsExactHistoricalRange()
    {
        var catalog = await _service!.CaptureAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            _repository!,
            new BlameRequest(
                Revision.Create("HEAD"),
                CreatePath("story file.txt"),
                new BlameRange(2, 3),
                DetectMoves: true,
                DetectCopies: true),
            TestContext.Current!.CancellationToken);

        Assert.IsNotNull(catalog.ResolvedRevision);
        Assert.AreEqual("alpha\nbeta revised\ngamma\n", Encoding.UTF8.GetString(catalog.Content.Span));
        Assert.HasCount(2, catalog.Attributions);
        TestSeq.AreEqual(s_expectedRangeLines, catalog.Attributions.Select(static item => item.ResultLineNumber));
        Assert.IsFalse(catalog.Attributions.Any(static item => item.Commit.IsUncommitted));
    }

    /// <summary>
    /// Verifies NUL-delimited path input creates a complete blame session and loads its focused line.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithPathspecFile_LoadsExactSinglePath()
    {
        var pathspecFile = Path.Combine(_temporaryDirectory!, "paths.bin");
        await File.WriteAllBytesAsync(
            pathspecFile,
            "story file.txt\0"u8.ToArray(),
            TestContext.Current!.CancellationToken);
        var session = await BlameSession.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            new BlameOptions(
                Revision: null,
                Paths: [],
                Line: 2,
                PathspecFile: pathspecFile,
                PathspecFileNul: true),
            CreateProcessEnvironment(),
            TestContext.Current!.CancellationToken);

        await session.LoadAsync(TestContext.Current!.CancellationToken);

        Assert.IsFalse(session.HasLoadFailure);
        Assert.AreEqual(2, session.State.FocusedItem!.Attribution.ResultLineNumber);
        Assert.AreEqual("story file.txt", session.PathDisplay);
        StringAssert.Contains(session.State.PreviewTitle, "Commit", StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies parent and back navigation retain exact requests while move and copy detection reload in place.
    /// </summary>
    [TestMethod]
    public async Task Session_WithParentBackAndDetectionControls_ReloadsExactLocations()
    {
        var session = await BlameSession.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            new BlameOptions(Revision: null, Paths: ["story file.txt"], Line: 2),
            CreateProcessEnvironment(),
            TestContext.Current!.CancellationToken);
        await session.LoadAsync(TestContext.Current.CancellationToken);
        var originalObjectId = session.State.FocusedItem!.Attribution.Commit.ObjectId.ToString();

        Assert.IsTrue(session.CanNavigateParent);
        await session.NavigateParentAsync(TestContext.Current.CancellationToken);

        Assert.IsTrue(session.CanNavigateBack);
        Assert.AreNotEqual("worktree", session.RevisionDisplay);
        Assert.AreNotEqual(originalObjectId, session.State.FocusedItem!.Attribution.Commit.ObjectId.ToString());

        await session.NavigateBackAsync(TestContext.Current.CancellationToken);
        Assert.AreEqual("worktree", session.RevisionDisplay);
        Assert.AreEqual(2, session.State.FocusedItem!.Attribution.ResultLineNumber);

        await session.ToggleMoveDetectionAsync(TestContext.Current.CancellationToken);
        await session.ToggleCopyDetectionAsync(TestContext.Current.CancellationToken);

        Assert.IsTrue(session.DetectMoves);
        Assert.IsTrue(session.DetectCopies);
        Assert.IsFalse(session.HasLoadFailure);
    }

    /// <summary>
    /// Verifies Unix path bytes that are not UTF-8 round trip through filesystem, argv, blame, and parsing.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task CaptureAsync_WithNonUtf8UnixPath_RetainsExactPathBytes()
    {
        var relativeBytes = new byte[] { (byte)'r', (byte)'a', (byte)'w', (byte)'-', 0xff, (byte)'.', (byte)'t', (byte)'x', (byte)'t' };
        var relativePath = GitPath.FromUnixBytes(relativeBytes);
        var hashResult = await RunGitAsync(
            "raw path\n"u8.ToArray(),
            ProcessArgument.Literal("hash-object"),
            ProcessArgument.Literal("-w"),
            ProcessArgument.Literal("--stdin"));
        var blob = Encoding.ASCII.GetString(hashResult.StandardOutput.Span).Trim();
        await RunGitAsync(
            ProcessArgument.Literal("update-index"),
            ProcessArgument.Literal("--add"),
            ProcessArgument.Literal("--cacheinfo"),
            ProcessArgument.Literal("100644"),
            ProcessArgument.Literal(blob),
            ProcessArgument.Native(relativePath));
        await RunGitAsync(
            ProcessArgument.Literal("commit"),
            ProcessArgument.Literal("--quiet"),
            ProcessArgument.Literal("--no-gpg-sign"),
            ProcessArgument.Literal("--message=raw path"));

        var catalog = await _service!.CaptureAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            _repository!,
            new BlameRequest(
                Revision.Create("HEAD"),
                relativePath,
                Range: null,
                DetectMoves: false,
                DetectCopies: false),
            TestContext.Current!.CancellationToken);

        Assert.HasCount(1, catalog.Attributions);
        TestSeq.AreEqual(relativeBytes, catalog.Path.GetUnixBytes().ToArray());
        TestSeq.AreEqual(relativeBytes, catalog.Attributions[0].SourcePath.GetUnixBytes().ToArray());
    }

    /// <summary>
    /// Verifies the real blame widget tree remains usable with keyboard and pointer input at 80 columns.
    /// </summary>
    [TestMethod]
    public async Task BlameView_AtEightyColumns_RendersSearchContextAndMouseActions()
    {
        var session = await BlameSession.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            new BlameOptions(Revision: null, Paths: ["story file.txt"], Line: 2),
            CreateProcessEnvironment(),
            TestContext.Current!.CancellationToken);
        await session.LoadAsync(TestContext.Current!.CancellationToken);
        var view = new BlameView(session, TestContext.Current.CancellationToken);
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(80, 24)
            .WithHex1bApp(
                options => options.EnableMouse = true,
                createdApplication =>
                {
                    application = createdApplication;
                    view.Attach(createdApplication);
                    return view.Build;
                })
            .Build();
        var runTask = terminal.RunAsync(timeout.Token);
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(5));

        try
        {
            await automator.WaitUntilTextAsync("beta revised", TimeSpan.FromSeconds(5));
            using (var initial = automator.CreateSnapshot())
            {
                Assert.IsTrue(initial.ContainsText("GitSail"));
                Assert.IsTrue(initial.ContainsText("blame"));
                Assert.IsTrue(initial.ContainsText("F6 Parent"));
                Assert.IsTrue(initial.ContainsText("F8 Back"));
                Assert.IsTrue(initial.ContainsText("Moves off"));
                Assert.IsTrue(initial.ContainsText("Copy path"));
                var moves = FindText(initial, "Moves off");
                await automator.ClickAtAsync(moves.X + 2, moves.Y, MouseButton.Left, timeout.Token);
            }


            await automator.WaitUntilAsync(
                snapshot => session.DetectMoves && snapshot.ContainsText("Moves on"),
                TimeSpan.FromSeconds(5),
                "Pointer activation toggles moved-line detection and reloads blame");
            using (var reloaded = automator.CreateSnapshot())
            {
                var find = FindText(reloaded, "Find: ");
                await automator.ClickAtAsync(find.X + 6, find.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("worktree only", timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.VisibleItems.Length == 1 &&
                    session.State.FocusedItem!.Attribution.Commit.IsUncommitted,
                TimeSpan.FromSeconds(5),
                "Blame search focuses the exact dirty worktree line");
            using var filtered = automator.CreateSnapshot();
            Assert.IsTrue(filtered.ContainsText("worktree only"));
            Assert.IsTrue(filtered.ContainsText("Uncommitted worktree line"));
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    private async Task RunGitAsync(params ProcessArgument[] arguments)
        => _ = await RunGitAsync(ReadOnlyMemory<byte>.Empty, arguments);

    private async Task<ProcessResult> RunGitAsync(
        ReadOnlyMemory<byte> standardInput,
        params ProcessArgument[] arguments)
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
            [.. arguments],
            CanonicalDirectory.Create(_temporaryDirectory!),
            environment,
            StandardInputSource.FromBytes(standardInput.Span),
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));
        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        return result;
    }

    private TestProcessEnvironment CreateProcessEnvironment()
        => new(new Dictionary<string, string?>
        {
            ["HOME"] = _temporaryDirectory,
            ["USERPROFILE"] = _temporaryDirectory,
            ["XDG_CONFIG_HOME"] = Path.Combine(_temporaryDirectory!, "xdg-config"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        });

    private static GitPath CreatePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(path));

    private static (int X, int Y) FindText(Hex1bTerminalSnapshot snapshot, string text)
    {
        for (var row = 0; row < snapshot.Height; row++)
        {
            var column = snapshot.GetLine(row).IndexOf(text, StringComparison.Ordinal);
            if (column >= 0)
            {
                return (column, row);
            }
        }

        Assert.Fail($"Text '{text}' was not found in the terminal snapshot.");
        return (-1, -1);
    }
}
