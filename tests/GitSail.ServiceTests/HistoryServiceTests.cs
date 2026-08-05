using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies structured history and exact commit previews against isolated real Git repositories.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class HistoryServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private HistoryService? _service;

    /// <summary>
    /// Creates an isolated two-commit repository for each structured-history test.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
        _installation = await new GitVersionService(resolver, _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            TestContext.Current!.CancellationToken);
        _service = new HistoryService(
            _installation,
            _runner,
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory));
        await RunGitAsync("init", "--quiet", "--initial-branch=main");
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "first.txt"),
            "first\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync("add", "--", "first.txt");
        await RunGitAsync("commit", "--quiet", "--no-gpg-sign", "--message=first commit");
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "second.txt"),
            "second\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync("add", "--", "second.txt");
        await RunGitAsync("commit", "--quiet", "--no-gpg-sign", "--message=second commit");
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
    /// Verifies default history returns structured commits and exact parent identities in display order.
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WithDefaultQuery_ReturnsStructuredHistory()
    {
        var catalog = await _service!.CaptureAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            HistoryQuery.CreateDefault(),
            TestContext.Current!.CancellationToken);

        Assert.HasCount(2, catalog.Commits);
        Assert.AreEqual("second commit", Encoding.UTF8.GetString(catalog.Commits[0].Subject.Span));
        Assert.HasCount(1, catalog.Commits[0].Parents);
        Assert.AreEqual(catalog.Commits[1].ObjectId, catalog.Commits[0].Parents[0]);
        Assert.AreEqual(CommitSignatureStatus.None, catalog.Commits[0].SignatureStatus);
    }

    /// <summary>
    /// Verifies exact native path restriction returns only commits that changed the selected path.
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WithPathspec_ReturnsMatchingCommit()
    {
        var path = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath("first.txt")
            : GitPath.FromUnixBytes("first.txt"u8);
        var query = new HistoryQuery(
            RevisionRange: null,
            Pathspecs: [path],
            MaximumCommitCount: 100);

        var catalog = await _service!.CaptureAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            query,
            TestContext.Current!.CancellationToken);

        Assert.HasCount(1, catalog.Commits);
        Assert.AreEqual("first commit", Encoding.UTF8.GetString(catalog.Commits[0].Subject.Span));
    }

    /// <summary>
    /// Verifies the selected exact object produces immutable commit details and a patch.
    /// </summary>
    [TestMethod]
    public async Task ShowAsync_WithSelectedCommit_ReturnsDetailsAndPatch()
    {
        var catalog = await _service!.CaptureAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            HistoryQuery.CreateDefault(),
            TestContext.Current!.CancellationToken);

        var output = await _service.ShowAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            catalog.Commits[0].ObjectId,
            TestContext.Current.CancellationToken);
        var text = Encoding.UTF8.GetString(output.Span);

        StringAssert.Contains(text, "second commit", StringComparison.Ordinal);
        StringAssert.Contains(text, "diff --git", StringComparison.Ordinal);
        StringAssert.Contains(text, "+second", StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies an option-looking revision is handled as data and returns a Git failure.
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WithOptionLookingRevision_ReturnsGitFailure()
    {
        var query = new HistoryQuery(
            Revision.Create("--help"),
            Pathspecs: [],
            MaximumCommitCount: 100);

        var exception = await Assert.ThrowsExactlyAsync<GitCommandException>(() => _service!.CaptureAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            query,
            TestContext.Current!.CancellationToken));

        Assert.AreNotEqual(0, exception.ExitCode);
    }

    /// <summary>
    /// Verifies an unborn current branch is presented as empty history instead of a fatal Git diagnostic.
    /// </summary>
    [TestMethod]
    public async Task CaptureAsync_WithUnbornHead_ReturnsEmptyHistory()
    {
        await RunGitAsync("switch", "--quiet", "--orphan", "empty");

        var catalog = await _service!.CaptureAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            HistoryQuery.CreateDefault(),
            TestContext.Current!.CancellationToken);

        Assert.IsEmpty(catalog.Commits);
    }

    /// <summary>
    /// Verifies NUL-delimited pathspec file input reaches the structured history request exactly.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithPathspecFile_LoadsMatchingCommitHistory()
    {
        var pathspecFile = Path.Combine(_temporaryDirectory!, "paths.bin");
        await File.WriteAllBytesAsync(
            pathspecFile,
            "first.txt\0"u8.ToArray(),
            TestContext.Current!.CancellationToken);
        var processEnvironment = CreateProcessEnvironment();
        using var session = await HistorySession.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            new HistoryOptions(
                RevisionRange: null,
                Pathspecs: [],
                PathspecFile: pathspecFile,
                PathspecFileNul: true),
            processEnvironment,
            TestContext.Current.CancellationToken);

        await session.LoadAsync(TestContext.Current.CancellationToken);

        Assert.IsFalse(session.HasLoadFailure);
        Assert.HasCount(1, session.State.Catalog!.Commits);
        Assert.AreEqual(
            "first commit",
            Encoding.UTF8.GetString(session.State.Catalog.Commits[0].Subject.Span));
    }

    /// <summary>
    /// Verifies the real history widget tree renders, filters, previews, and responds to pointer input.
    /// </summary>
    [TestMethod]
    public async Task HistoryView_WithKeyboardAndMouse_RendersAndFiltersExactCommits()
    {
        var processEnvironment = CreateProcessEnvironment();
        using var session = await HistorySession.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            new HistoryOptions(RevisionRange: null, Pathspecs: []),
            processEnvironment,
            TestContext.Current!.CancellationToken);
        await session.LoadAsync(TestContext.Current.CancellationToken);
        var view = new HistoryView(session, TestContext.Current.CancellationToken);
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(120, 30)
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
            await automator.WaitUntilTextAsync("second commit", TimeSpan.FromSeconds(5));
            await automator.WaitUntilTextAsync("+second", TimeSpan.FromSeconds(5));
            using (var initial = automator.CreateSnapshot())
            {
                Assert.IsTrue(initial.ContainsText("GitSail"));
                Assert.IsTrue(initial.ContainsText("history"));
                Assert.IsTrue(initial.ContainsText("F7 Find"));
                Assert.IsTrue(initial.ContainsText("Mouse Select/Scroll/Resize"));
                var subject = FindText(initial, "second commit");
                Assert.IsLessThan(40, subject.X, "The commit subject must remain visible in the history list.");
                var find = FindText(initial, "Find: ");
                await automator.ClickAtAsync(find.X + 6, find.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("first commit", timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.VisibleItems.Length == 1 &&
                    Encoding.UTF8.GetString(session.State.FocusedItem!.Commit.Subject.Span) == "first commit",
                TimeSpan.FromSeconds(5),
                "History search focuses the only exact matching commit");
            await automator.WaitUntilTextAsync("+first", TimeSpan.FromSeconds(5));
            using var filtered = automator.CreateSnapshot();
            Assert.IsTrue(filtered.ContainsText("first commit"));
            Assert.IsFalse(filtered.ContainsText("second commit"));
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies pointer dismissal and confirmation run an exact commit revert from history.
    /// </summary>
    [TestMethod]
    public async Task HistoryView_WithCommitRevert_ConfirmsAndRunsExactOperation()
    {
        var processEnvironment = CreateProcessEnvironment();
        using var session = await HistorySession.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            new HistoryOptions(RevisionRange: null, Pathspecs: []),
            processEnvironment,
            TestContext.Current!.CancellationToken);
        await session.LoadAsync(TestContext.Current.CancellationToken);
        var selectedObjectId = session.State.FocusedItem!.Commit.ObjectId;
        var view = new HistoryView(session, TestContext.Current.CancellationToken);
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(120, 30)
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
            await automator.WaitUntilTextAsync("second commit", TimeSpan.FromSeconds(5));
            await automator.KeyAsync(Hex1bKey.R, timeout.Token);
            await automator.WaitUntilTextAsync("Revert commit this commit?", TimeSpan.FromSeconds(8));
            using (var confirmation = automator.CreateSnapshot())
            {
                Assert.IsTrue(confirmation.ContainsText($"Commit: {selectedObjectId}"));
                Assert.IsTrue(confirmation.ContainsText("Current target: branch main"));
                Assert.IsTrue(confirmation.ContainsText("Cancel"));
                await automator.ClickAtAsync(0, 0, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Revert commit this commit?"),
                TimeSpan.FromSeconds(5),
                "Clicking outside the history confirmation closes it");
            using (var workspace = automator.CreateSnapshot())
            {
                var revertAction = FindText(workspace, "Revert commit...");
                await automator.ClickAtAsync(
                    revertAction.X + 1,
                    revertAction.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Revert commit this commit?", TimeSpan.FromSeconds(8));
            using (var confirmation = automator.CreateSnapshot())
            {
                var approval = FindTextOnLineWith(confirmation, "Revert commit", "Cancel");
                await automator.ClickAtAsync(
                    approval.X + 1,
                    approval.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => session.PendingOperation is null &&
                    session.State.Catalog?.Commits.Length == 3 &&
                    !File.Exists(Path.Combine(_temporaryDirectory!, "second.txt")) &&
                    !snapshot.ContainsText("Revert commit this commit?"),
                TimeSpan.FromSeconds(12),
                "The confirmed commit revert refreshes history and removes the committed file");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies supported compact terminal sizes keep history actions and shortcuts readable.
    /// </summary>
    [TestMethod]
    public async Task HistoryView_AtCompactSupportedSizes_KeepsControlsReadable()
    {
        await VerifyCompactLayoutAsync(width: 60, height: 18);
        await VerifyCompactLayoutAsync(width: 80, height: 24);
    }

    private async Task RunGitAsync(params string[] arguments)
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
            CanonicalDirectory.Create(_temporaryDirectory!),
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));

        var result = await _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);

        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
    }

    private TestProcessEnvironment CreateProcessEnvironment()
        => new(new Dictionary<string, string?>
        {
            ["HOME"] = _temporaryDirectory,
            ["USERPROFILE"] = _temporaryDirectory,
            ["XDG_CONFIG_HOME"] = Path.Combine(_temporaryDirectory!, "xdg-config"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_AUTHOR_NAME"] = "GitSail Test",
            ["GIT_AUTHOR_EMAIL"] = "gitsail@example.invalid",
            ["GIT_COMMITTER_NAME"] = "GitSail Test",
            ["GIT_COMMITTER_EMAIL"] = "gitsail@example.invalid",
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        });

    private async Task VerifyCompactLayoutAsync(int width, int height)
    {
        using var session = await HistorySession.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            new HistoryOptions(RevisionRange: null, Pathspecs: []),
            CreateProcessEnvironment(),
            TestContext.Current!.CancellationToken);
        await session.LoadAsync(TestContext.Current.CancellationToken);
        var view = new HistoryView(session, TestContext.Current.CancellationToken);
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(width, height)
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
            await automator.WaitUntilTextAsync("second commit", TimeSpan.FromSeconds(5));
            using (var compact = automator.CreateSnapshot())
            {
                Assert.IsTrue(compact.ContainsText("Pick..."), $"Pick action was clipped at {width}x{height}.");
                Assert.IsTrue(compact.ContainsText("Revert..."), $"Revert action was clipped at {width}x{height}.");
                Assert.IsTrue(compact.ContainsText("C Pick"), $"Pick shortcut was clipped at {width}x{height}.");
                Assert.IsTrue(compact.ContainsText("R Revert"), $"Revert shortcut was clipped at {width}x{height}.");
                Assert.IsTrue(compact.ContainsText("Ctrl+Q Quit"), $"Quit shortcut was clipped at {width}x{height}.");
            }

            await automator.KeyAsync(Hex1bKey.R, timeout.Token);
            await automator.WaitUntilTextAsync("Revert commit this commit?", TimeSpan.FromSeconds(8));
            using (var confirmation = automator.CreateSnapshot())
            {
                AssertWindowFrameIsComplete(
                    confirmation,
                    "Revert commit this commit?",
                    Math.Min(76, width - 2),
                    Math.Min(12, height - 2));
                Assert.IsTrue(confirmation.ContainsText("Cancel"));
                Assert.IsTrue(confirmation.ContainsText("Revert commit"));
                await automator.ClickAtAsync(0, 0, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Revert commit this commit?"),
                TimeSpan.FromSeconds(5),
                $"Compact {width}x{height} confirmation closes on an outside pointer click");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    private static void AssertWindowFrameIsComplete(
        Hex1bTerminalSnapshot snapshot,
        string title,
        int expectedWidth,
        int expectedHeight)
    {
        var titlePosition = FindText(snapshot, title);
        var left = titlePosition.X - 1;
        var top = titlePosition.Y - 1;
        var right = left + expectedWidth - 1;
        var bottom = top + expectedHeight - 1;

        Assert.IsGreaterThanOrEqualTo(0, left);
        Assert.IsGreaterThanOrEqualTo(0, top);
        Assert.IsLessThan(snapshot.Width, right);
        Assert.IsLessThan(snapshot.Height, bottom);
        Assert.AreEqual("┌", snapshot.GetCell(left, top).Character);
        Assert.AreEqual("┐", snapshot.GetCell(right, top).Character);
        Assert.AreEqual("└", snapshot.GetCell(left, bottom).Character);
        Assert.AreEqual("┘", snapshot.GetCell(right, bottom).Character);
    }

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

    private static (int X, int Y) FindTextOnLineWith(
        Hex1bTerminalSnapshot snapshot,
        string text,
        string companion)
    {
        for (var row = 0; row < snapshot.Height; row++)
        {
            var line = snapshot.GetLine(row);
            var column = line.IndexOf(text, StringComparison.Ordinal);
            if (column >= 0 && line.Contains(companion, StringComparison.Ordinal))
            {
                return (column, row);
            }
        }

        Assert.Fail($"Text '{text}' was not found on a line with '{companion}'.");
        return (-1, -1);
    }
}
