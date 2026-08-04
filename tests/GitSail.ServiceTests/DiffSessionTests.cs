using GitSail.CommandLine;
using GitSail.Git.Execution;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using Hex1b.Theming;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies the dedicated comparison session and responsive pointer UI against a real repository.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DiffSessionTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;

    /// <summary>
    /// Creates an isolated repository with two commits and two changed files.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-diff-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
        _installation = await new GitVersionService(resolver, _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
        await RunGitAsync("init", "--quiet", "--initial-branch=main");
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "selected file.txt"),
            "baseline selected\ncontext one\ncontext two\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "other.txt"),
            "baseline other\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync("add", "--all");
        await RunGitAsync("commit", "--quiet", "--no-gpg-sign", "--message=baseline");
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "selected file.txt"),
            "committed selected\ncontext one\ncontext two\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "other.txt"),
            "committed other\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync("add", "--all");
        await RunGitAsync("commit", "--quiet", "--no-gpg-sign", "--message=second");
    }

    /// <summary>
    /// Removes the isolated repository and home after each comparison test.
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
    /// Verifies resolved commit pairs and native pathspecs produce aligned and unified presentations.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithCommitPairAndPathspec_LoadsExactComparison()
    {
        using var session = await DiffSession.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            new DiffOptions(
                Cached: false,
                LeftRevision: "HEAD~1",
                RightRevision: "HEAD",
                Pathspecs: ["selected file.txt"]),
            CreateProcessEnvironment(),
            TestContext.Current!.CancellationToken);

        await session.LoadAsync(TestContext.Current.CancellationToken);

        Assert.IsFalse(session.HasLoadFailure, session.Activity);
        Assert.HasCount(1, session.State.VisibleItems);
        Assert.AreEqual("selected file.txt", session.State.FocusedItem!.File.NewPath.DisplayText);
        StringAssert.StartsWith(session.LeftLabel, "HEAD~1 (");
        StringAssert.StartsWith(session.RightLabel, "HEAD (");
        StringAssert.Contains(session.State.LeftEditor.Document.GetText(), "-baseline selected");
        StringAssert.Contains(session.State.RightEditor.Document.GetText(), "+committed selected");
        StringAssert.Contains(session.State.UnifiedEditor.Document.GetText(), "diff --git");
        StringAssert.Contains(session.GetUnifiedPresentation(), "+committed selected");

        await session.ChangeContextAsync(-1, TestContext.Current.CancellationToken);

        Assert.AreEqual(2, session.ContextLines);
        Assert.IsFalse(session.HasLoadFailure, session.Activity);
        StringAssert.Contains(session.GetUnifiedPresentation(), "+committed selected");
    }

    /// <summary>
    /// Verifies compact and wide layouts keep headers and actions readable while mouse input changes views.
    /// </summary>
    /// <param name="width">The terminal width under test.</param>
    /// <param name="height">The terminal height under test.</param>
    [TestMethod]
    [DataRow(80, 24)]
    [DataRow(120, 30)]
    public async Task DiffView_WithKeyboardAndMouse_RendersWithoutCutoff(int width, int height)
    {
        using var session = await DiffSession.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            new DiffOptions(
                Cached: false,
                LeftRevision: "HEAD~1",
                RightRevision: "HEAD",
                Pathspecs: []),
            CreateProcessEnvironment(),
            TestContext.Current!.CancellationToken);
        await session.LoadAsync(TestContext.Current.CancellationToken);
        var view = new DiffView(session, TestContext.Current.CancellationToken);
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
            await automator.WaitUntilTextAsync("selected file.txt", TimeSpan.FromSeconds(5));
            using (var initial = automator.CreateSnapshot())
            {
                var firstHeader = initial.GetLine(0);
                var secondHeader = initial.GetLine(1);
                StringAssert.Contains(firstHeader, "GitSail");
                StringAssert.Contains(firstHeader, "diff");
                StringAssert.Contains(firstHeader, $"Git {_installation!.Version}");
                StringAssert.Contains(secondHeader, "HEAD~1");
                StringAssert.Contains(secondHeader, RepositoryLabel.Create(session.Repository));
                Assert.IsTrue(initial.ContainsText("Ctrl+Q Quit"));
                Assert.IsTrue(initial.ContainsText("Quit"));
                var selected = FindText(initial, "selected file.txt");
                await automator.ClickAtAsync(
                    selected.X + 1,
                    selected.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.State.FocusedItem?.File.NewPath.DisplayText == "selected file.txt",
                TimeSpan.FromSeconds(5),
                "Mouse selection focuses the exact changed file");
            await automator.WaitUntilTextAsync("+committed selected", TimeSpan.FromSeconds(5));
            using (var selectedPatch = automator.CreateSnapshot())
            {
                var position = FindText(selectedPatch, "+committed selected");
                var expectedForeground = Hex1bColor.FromRgb(80, 220, 80);
                var expectedBackground = Hex1bColor.FromRgb(20, 40, 20);
                for (var offset = 0; offset < "+committed selected".Length; offset++)
                {
                    var cell = selectedPatch.GetCell(position.X + offset, position.Y);
                    Assert.AreEqual(expectedForeground, cell.Foreground);
                    Assert.AreEqual(expectedBackground, cell.Background);
                }

                var toggleLabel = width >= 100 ? "Unified" : "View";
                var toggle = FindText(selectedPatch, toggleLabel);
                await automator.ClickAtAsync(toggle.X + 1, toggle.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => !session.State.IsSideBySide,
                TimeSpan.FromSeconds(5),
                "Mouse activation switches to the unified comparison");
            await automator.WaitUntilTextAsync("diff --git", TimeSpan.FromSeconds(5));
            using var unified = automator.CreateSnapshot();
            Assert.IsTrue(unified.ContainsText("Unified: selected file.txt"));
            Assert.IsTrue(unified.ContainsText("+committed selected"));
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
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
