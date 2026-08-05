using GitSail.CommandLine;
using GitSail.Git.Execution;
using GitSail.Localization.Generated;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using Hex1b.Theming;
using System.Globalization;
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
        var baselineLines = Enumerable.Range(1, 120)
            .Select(static line => $"unchanged line {line}")
            .ToArray();
        baselineLines[0] = "baseline selected";
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "selected file.txt"),
            string.Join('\n', baselineLines) + "\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "other.txt"),
            "baseline other\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync("add", "--all");
        await RunGitAsync("commit", "--quiet", "--no-gpg-sign", "--message=baseline");
        var committedLines = baselineLines.ToArray();
        committedLines[0] = "committed selected";
        committedLines[14] = "committed line 15";
        committedLines[34] = "committed line 35";
        committedLines[54] = "committed line 55";
        committedLines[69] = "committed line 70";
        committedLines[89] = "committed line 90";
        committedLines[104] = "committed line 105";
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "selected file.txt"),
            string.Join('\n', committedLines) + "\n",
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

        Assert.AreEqual(4, session.ContextLines);
        Assert.AreEqual(8, session.State.UnifiedEditor.TabSize);
        Assert.IsFalse(session.HasLoadFailure, session.Activity);
        StringAssert.Contains(session.GetUnifiedPresentation(), "+committed selected");
    }

    /// <summary>
    /// Verifies standalone comparisons load configured context, options, and tab presentation before capture.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithDiffConfiguration_AppliesRuntimeValues()
    {
        await RunGitAsync("config", "--local", "gui.diffcontext", "7");
        await RunGitAsync("config", "--local", "gui.diffopts", "--histogram --stat --numstat");
        await RunGitAsync("config", "--local", "gui.tabsize", "6");
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

        Assert.IsFalse(session.HasLoadFailure, session.Activity);
        Assert.AreEqual(7, session.ContextLines);
        Assert.AreEqual(6, session.State.UnifiedEditor.TabSize);
        Assert.AreEqual(6, session.State.LeftEditor.TabSize);
        Assert.AreEqual(6, session.State.RightEditor.TabSize);
        Assert.IsGreaterThan(0, session.State.VisibleItems.Length);
    }

    /// <summary>
    /// Verifies compact and wide layouts keep headers and actions readable while mouse input changes views.
    /// </summary>
    /// <param name="width">The terminal width under test.</param>
    /// <param name="height">The terminal height under test.</param>
    [TestMethod]
    [DataRow(60, 18)]
    [DataRow(80, 24)]
    [DataRow(120, 30)]
    [DataRow(160, 36)]
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
                var identityHeader = width >= 130 ? firstHeader : secondHeader;
                StringAssert.Contains(identityHeader, "HEAD~1");
                StringAssert.Contains(identityHeader, RepositoryLabel.Create(session.Repository));
                Assert.IsTrue(
                    initial.ContainsText("Ctrl+Q Quit"),
                    string.Join(
                        Environment.NewLine,
                        Enumerable.Range(0, initial.Height).Select(initial.GetLine)));
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
                var deletedPosition = FindText(selectedPatch, "-baseline selected");
                Assert.AreEqual("│", selectedPatch.GetCell(deletedPosition.X - 1, deletedPosition.Y).Character);
                Assert.AreEqual("1", selectedPatch.GetCell(deletedPosition.X - 2, deletedPosition.Y).Character);
                var position = FindText(selectedPatch, "+committed selected");
                Assert.AreEqual("│", selectedPatch.GetCell(position.X - 1, position.Y).Character);
                Assert.AreEqual("1", selectedPatch.GetCell(position.X - 2, position.Y).Character);
                var expectedForeground = Hex1bColor.FromRgb(80, 220, 80);
                var expectedLineBackground = Hex1bColor.FromRgb(20, 40, 20);
                var expectedIntralineBackground = Hex1bColor.FromRgb(35, 85, 35);
                for (var offset = 0; offset < "+committed selected".Length; offset++)
                {
                    var cell = selectedPatch.GetCell(position.X + offset, position.Y);
                    Assert.AreEqual(expectedForeground, cell.Foreground);
                    var expectedBackground = offset is >= 1 and <= 9
                        ? expectedIntralineBackground
                        : expectedLineBackground;
                    Assert.AreEqual(expectedBackground, cell.Background);
                }

                var leftEditor = application!.Focusables
                    .OfType<EditorNode>()
                    .Single(editor => ReferenceEquals(editor.State, session.State.LeftEditor));
                var rightEditor = application.Focusables
                    .OfType<EditorNode>()
                    .Single(editor => ReferenceEquals(editor.State, session.State.RightEditor));
                await automator.MouseMoveToAsync(
                    rightEditor.Bounds.X + Math.Min(5, rightEditor.Bounds.Width - 1),
                    rightEditor.Bounds.Y + Math.Min(2, rightEditor.Bounds.Height - 1),
                    timeout.Token);
                await automator.ScrollDownAsync(2, timeout.Token);
                await automator.WaitUntilAsync(
                    _ => leftEditor.ScrollOffset > 1 &&
                        leftEditor.ScrollOffset == rightEditor.ScrollOffset,
                    TimeSpan.FromSeconds(5),
                    "Mouse wheel scrolling keeps both aligned editors on the same row");

                var toggleLabel = width switch
                {
                    < 80 => "V",
                    < 130 => AppMessages.DiffActionView,
                    _ => AppMessages.DiffActionUnified,
                };
                var toggle = FindText(selectedPatch, toggleLabel);
                await automator.ClickAtAsync(toggle.X + 1, toggle.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => !session.State.IsSideBySide,
                TimeSpan.FromSeconds(5),
                "Mouse activation switches to the unified comparison");
            await automator.WaitUntilTextAsync("diff --git", TimeSpan.FromSeconds(5));
            var scrollToFirstChange = false;
            using (var unifiedTop = automator.CreateSnapshot())
            {
                Assert.IsTrue(unifiedTop.ContainsText("Unified: selected file.txt"));
                Assert.IsTrue(unifiedTop.ContainsText("diff --git"));
                scrollToFirstChange = !unifiedTop.ContainsText("+committed selected");
            }

            if (scrollToFirstChange)
            {
                var editor = application!.Focusables
                    .OfType<EditorNode>()
                    .Single(node => ReferenceEquals(node.State, session.State.UnifiedEditor));
                await automator.MouseMoveToAsync(
                    editor.Bounds.X + Math.Min(5, editor.Bounds.Width - 1),
                    editor.Bounds.Y + Math.Min(2, editor.Bounds.Height - 1),
                    timeout.Token);
                await automator.ScrollDownAsync(1, timeout.Token);
                await automator.WaitUntilTextAsync(
                    "+committed selected",
                    TimeSpan.FromSeconds(5));
            }

            using var unified = automator.CreateSnapshot();
            var unifiedDeletion = FindText(unified, "-baseline selected");
            Assert.AreEqual("1", unified.GetCell(unifiedDeletion.X - 6, unifiedDeletion.Y).Character);
            Assert.AreEqual(" ", unified.GetCell(unifiedDeletion.X - 2, unifiedDeletion.Y).Character);
            var unifiedAddition = FindText(unified, "+committed selected");
            Assert.AreEqual(" ", unified.GetCell(unifiedAddition.X - 6, unifiedAddition.Y).Character);
            Assert.AreEqual("1", unified.GetCell(unifiedAddition.X - 2, unifiedAddition.Y).Character);
            var textControl = FindText(unified, "Text");
            await automator.ClickAtAsync(
                textControl.X + 1,
                textControl.Y,
                MouseButton.Left,
                timeout.Token);
            await automator.WaitUntilTextAsync("Text: ", TimeSpan.FromSeconds(5));
            using (var textInput = automator.CreateSnapshot())
            {
                var textSearch = FindText(textInput, "Text: ");
                await automator.ClickAtAsync(
                    textSearch.X + 6,
                    textSearch.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.TypeAsync("committed line 105", timeout.Token);
            await automator.EnterAsync(timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.UnifiedEditor.Cursor.HasSelection &&
                    session.State.UnifiedEditor.Document.GetText(
                        session.State.UnifiedEditor.Cursor.SelectionRange) == "committed line 105",
                TimeSpan.FromSeconds(5),
                "Submitted content search selects the exact unified match");
            await automator.WaitUntilTextAsync("1/1", TimeSpan.FromSeconds(5));
            using (var searched = automator.CreateSnapshot())
            {
                var lineControl = FindText(searched, "Line");
                await automator.ClickAtAsync(
                    lineControl.X + 1,
                    lineControl.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.WaitUntilTextAsync("Line: ", TimeSpan.FromSeconds(5));
            using (var lineNavigation = automator.CreateSnapshot())
            {
                var lineInput = FindText(lineNavigation, "Line: ");
                await automator.ClickAtAsync(
                    lineInput.X + 6,
                    lineInput.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.TypeAsync("4", timeout.Token);
            await automator.EnterAsync(timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.UnifiedEditor.Document.OffsetToPosition(
                    session.State.UnifiedEditor.Cursor.Position).Line == 4,
                TimeSpan.FromSeconds(5),
                "Submitted line navigation focuses the exact one-based presentation line");
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Line: "),
                TimeSpan.FromSeconds(5),
                "Escape hides comparison line navigation");
            await automator.KeyAsync(Hex1bKey.F7, timeout.Token);
            await automator.WaitUntilTextAsync("Paths: ", TimeSpan.FromSeconds(5));
            await automator.WaitUntilAsync(
                _ => application!.FocusedNode is TextBoxNode,
                TimeSpan.FromSeconds(5),
                "F7 focuses changed-path filtering");
            await automator.KeyAsync(Hex1bKey.Escape, timeout.Token);
            await automator.WaitUntilAsync(
                snapshot => !snapshot.ContainsText("Paths: "),
                TimeSpan.FromSeconds(5),
                "Escape hides changed-path filtering");
            var unifiedEditor = application!.Focusables
                .OfType<EditorNode>()
                .Single(editor => ReferenceEquals(editor.State, session.State.UnifiedEditor));
            await automator.ClickAtAsync(
                unifiedEditor.Bounds.X + Math.Min(5, unifiedEditor.Bounds.Width - 1),
                unifiedEditor.Bounds.Y + Math.Min(2, unifiedEditor.Bounds.Height - 1),
                MouseButton.Left,
                timeout.Token);
            await automator.WaitUntilAsync(
                _ => ReferenceEquals(application.FocusedNode, unifiedEditor),
                TimeSpan.FromSeconds(5),
                "Pointer input focuses the unified read-only editor");
            var documentBeforeTyping = session.State.UnifiedEditor.Document.GetText();
            await automator.TypeAsync("x", timeout.Token);
            Assert.AreEqual(documentBeforeTyping, session.State.UnifiedEditor.Document.GetText());
            await automator.KeyAsync(Hex1bKey.J, timeout.Token);
            await automator.KeyAsync(Hex1bKey.J, timeout.Token);
            await automator.KeyAsync(Hex1bKey.J, timeout.Token);
            await automator.WaitUntilAsync(
                _ => unifiedEditor.ScrollOffset > 1,
                TimeSpan.FromSeconds(5),
                "Hunk navigation scrolls the read-only editor to the selected hunk");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies translated and expansion-pseudo comparison controls fit and remain mouse-operable.
    /// </summary>
    /// <param name="width">The terminal width under test.</param>
    /// <param name="height">The terminal height under test.</param>
    /// <param name="locale">The UI culture used to build the comparison.</param>
    [TestMethod]
    [DataRow(60, 18, "ja-JP")]
    [DataRow(80, 24, "de-DE")]
    [DataRow(120, 30, "ru-RU")]
    [DataRow(160, 36, "en-XA")]
    public async Task DiffView_WithLocalizedText_FitsResponsiveBreakpoint(
        int width,
        int height,
        string locale)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.GetCultureInfo(locale);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current!.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            using var session = await DiffSession.OpenAsync(
                CanonicalDirectory.Create(_temporaryDirectory!),
                new DiffOptions(
                    Cached: false,
                    LeftRevision: "HEAD~1",
                    RightRevision: "HEAD",
                    Pathspecs: []),
                CreateProcessEnvironment(),
                timeout.Token);
            await session.LoadAsync(timeout.Token);
            var view = new DiffView(session, timeout.Token);
            Hex1bApp? application = null;
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
                using var snapshot = automator.CreateSnapshot();
                Assert.IsTrue(snapshot.ContainsText(
                    AppMessages.DiffTitleChangedFilesForLocale(locale, 2)));
                Assert.IsTrue(snapshot.ContainsText(
                    AppMessages.WorkspaceActionPathsForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(AppMessages.DiffActionTextForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(AppMessages.DiffActionLineForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(
                    AppMessages.WorkspaceActionQuitForLocale(locale)));

                var toggleLabel = width switch
                {
                    < 80 => "V",
                    < 130 => AppMessages.DiffActionViewForLocale(locale),
                    _ => AppMessages.DiffActionUnifiedForLocale(locale),
                };
                var toggle = FindText(snapshot, toggleLabel);
                await automator.ClickAtAsync(
                    toggle.X + Math.Min(1, toggleLabel.Length - 1),
                    toggle.Y,
                    MouseButton.Left,
                    timeout.Token);
                await automator.WaitUntilAsync(
                    _ => !session.State.IsSideBySide,
                    TimeSpan.FromSeconds(5),
                    $"Localized layout toggle remains clickable at {width}x{height}");

                for (var row = 0; row < snapshot.Height; row++)
                {
                    Assert.IsLessThanOrEqualTo(
                        snapshot.Width,
                        DisplayWidth.GetStringWidth(snapshot.GetLine(row).TrimEnd()),
                        $"Locale '{locale}' overflowed terminal row {row} at {width}x{height}.");
                }
            }
            finally
            {
                application?.RequestStop();
                await runTask;
                view.Detach();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    /// <summary>
    /// Verifies dimensions below the supported minimum show a readable resize screen with keyboard and mouse exit paths.
    /// </summary>
    /// <param name="width">The terminal width under test.</param>
    /// <param name="height">The terminal height under test.</param>
    [TestMethod]
    [DataRow(59, 18)]
    [DataRow(60, 17)]
    public async Task DiffView_BelowMinimum_ShowsResizeScreenAndMouseQuit(int width, int height)
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
        var view = new DiffView(session, TestContext.Current.CancellationToken);
        Hex1bApp? application = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
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
            await automator.WaitUntilTextAsync(
                AppMessages.WorkspaceResizeTitle,
                TimeSpan.FromSeconds(5));
            using var snapshot = automator.CreateSnapshot();
            Assert.IsTrue(snapshot.ContainsText(
                "GitSail needs a terminal at least 60 columns wide and 18"));
            Assert.IsTrue(snapshot.ContainsText("rows high."));
            Assert.IsTrue(snapshot.ContainsText(
                $"Ctrl+Q {AppMessages.WorkspaceActionQuit}"));
            Assert.IsFalse(snapshot.ContainsText(AppMessages.DiffTitleChangedFiles(2)));
            var quit = FindText(snapshot, AppMessages.WorkspaceActionQuit);
            await automator.ClickAtAsync(
                quit.X + 1,
                quit.Y,
                MouseButton.Left,
                timeout.Token);
            await runTask;
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
