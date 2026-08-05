using GitSail.CommandLine;
using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Localization.Generated;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Globalization;
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
        var secondFile = new StringBuilder("second stale-preview-tail-}],\n");
        for (var line = 1; line <= 80; line++)
        {
            secondFile.Append($"scroll-row-{line:D2}");
            if (line % 2 != 0)
            {
                secondFile.Append(" short");
            }
            else
            {
                secondFile.Append(" horizontal-preview-tail-abcdefghijklmnopqrstuvwxyz-0123456789-}],");
            }

            secondFile.Append('\n');
        }

        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "second.txt"),
            secondFile.ToString(),
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
    /// Verifies rapid list focus changes complete immediately and load only the settled preview.
    /// </summary>
    [TestMethod]
    public async Task HistorySession_WithRapidFocusChanges_KeepsListMovementImmediate()
    {
        using var session = await HistorySession.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            new HistoryOptions(RevisionRange: null, Pathspecs: []),
            CreateProcessEnvironment(),
            TestContext.Current!.CancellationToken);
        await session.LoadAsync(TestContext.Current.CancellationToken);

        for (var change = 0; change < 100; change++)
        {
            var focusTask = session.FocusAsync(
                change % session.State.VisibleItems.Length,
                TestContext.Current.CancellationToken);
            Assert.IsTrue(
                focusTask.IsCompletedSuccessfully,
                "History list focus must not wait for commit preview capture.");
        }

        var expectedCommit = session.State.FocusedItem!.Commit;
        var expectedTitle = AppMessages.HistoryPreviewCommitTitle(
            expectedCommit.ObjectId.ToString()[..12]);
        for (var attempt = 0;
             attempt < 100 && !string.Equals(
                 session.State.PreviewTitle,
                 expectedTitle,
                 StringComparison.Ordinal);
             attempt++)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(50),
                TestContext.Current.CancellationToken);
        }

        Assert.AreEqual(expectedTitle, session.State.PreviewTitle);
        StringAssert.Contains(
            session.State.Preview.Document.GetText(),
            "first commit",
            StringComparison.Ordinal);
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
        var cleanRepaintCount = 0;
        await using var terminalSession = new TerminalApplicationSession(
            view.Build,
            new Hex1bAppOptions
            {
                EnableMouse = true,
            },
            new HeadlessPresentationAdapter(
                120,
                30,
                new TerminalCapabilities
                {
                    SupportsMouse = true,
                    SupportsTrueColor = true,
                    Supports256Colors = true,
                    SupportsAlternateScreen = true,
                    HandlesAlternateScreenNatively = false,
                    SupportsBracketedPaste = true,
                    SupportsStyledUnderlines = true,
                    SupportsUnderlineColor = true,
                }));
        application = terminalSession.Application;
        view.Attach(
            application,
            () =>
            {
                Interlocked.Increment(ref cleanRepaintCount);
                terminalSession.RequestCleanRepaint();
            });
        var terminal = terminalSession.Terminal;
        var runTask = terminalSession.RunAsync(timeout.Token);
        var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(5));
        var staleTailEnd = (X: -1, Y: -1);
        var previewPoint = (X: -1, Y: -1);

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
                Assert.IsTrue(initial.ContainsText("Date: "));
                Assert.IsTrue(initial.ContainsText("References: HEAD -> refs/heads/main"));
                var subject = FindText(initial, "second commit");
                Assert.IsLessThan(40, subject.X, "The commit subject must remain visible in the history list.");
                var staleTail = FindText(initial, "stale-preview-tail-}],");
                staleTailEnd = (staleTail.X + "stale-preview-tail-}],".Length - 1, staleTail.Y);
                previewPoint = (staleTail.X, staleTail.Y);
                await automator.ClickAtAsync(
                    previewPoint.X,
                    previewPoint.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            var previewEditor = application!.Focusables
                .OfType<EditorNode>()
                .Single(node => ReferenceEquals(node.State, session.State.Preview));
            var repaintCountBeforeScrollbarDrag = Volatile.Read(ref cleanRepaintCount);
            var scrollbarX = previewEditor.Bounds.X + previewEditor.Bounds.Width - 1;
            await automator.DragAsync(
                scrollbarX,
                previewEditor.Bounds.Y,
                scrollbarX,
                previewEditor.Bounds.Y + 5,
                MouseButton.Left,
                timeout.Token);
            await automator.WaitUntilAsync(
                _ => previewEditor.ScrollOffset > 1,
                TimeSpan.FromSeconds(5),
                "History preview vertical scrollbar drag moves the viewport");
            await automator.WaitUntilAsync(
                _ => Volatile.Read(ref cleanRepaintCount) > repaintCountBeforeScrollbarDrag,
                TimeSpan.FromSeconds(5),
                "History preview scrollbar dragging requests a clean repaint");
            Assert.IsGreaterThan(
                repaintCountBeforeScrollbarDrag,
                Volatile.Read(ref cleanRepaintCount),
                "History preview scrollbar dragging must request a clean repaint.");
            await automator.Ctrl().KeyAsync(Hex1bKey.Home, timeout.Token);
            await automator.WaitUntilAsync(
                _ => previewEditor.ScrollOffset == 1,
                TimeSpan.FromSeconds(5),
                "History preview returns to the first row after scrollbar testing");

            await automator.ScrollDownAsync(10, timeout.Token);
            await automator.WaitUntilTextAsync("+scroll-row-20", TimeSpan.FromSeconds(5));
            using (var scrolledDown = automator.CreateSnapshot())
            {
                Assert.IsFalse(scrolledDown.ContainsText("stale-preview-tail-}],"));
                Assert.IsTrue(scrolledDown.ContainsText("+scroll-row-20"));
            }

            await automator.ScrollUpAsync(10, timeout.Token);
            await automator.WaitUntilTextAsync("stale-preview-tail-}],", TimeSpan.FromSeconds(5));
            using (var beforeHorizontalScroll = automator.CreateSnapshot())
            {
                Assert.IsTrue(beforeHorizontalScroll.ContainsText(
                    "+scroll-row-02 horizontal-preview-tail"));
            }

            await new Hex1bTerminalInputSequenceBuilder()
                .MouseMoveTo(previewPoint.X, previewPoint.Y)
                .Shift()
                .ScrollDown(20)
                .WaitUntil(
                    snapshot => !snapshot.ContainsText("+scroll-row-02 horizontal-preview-tail"),
                    TimeSpan.FromSeconds(5),
                    "History preview scrolls horizontally to the right")
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            using (var scrolledRight = automator.CreateSnapshot())
            {
                Assert.IsFalse(scrolledRight.ContainsText("+scroll-row-02 horizontal-preview-tail"));
            }
            Assert.IsGreaterThan(
                0,
                Volatile.Read(ref cleanRepaintCount),
                "Preview scrolling must request a clean repaint when its viewport moves.");

            await new Hex1bTerminalInputSequenceBuilder()
                .MouseMoveTo(previewPoint.X, previewPoint.Y)
                .Shift()
                .ScrollUp(20)
                .WaitUntil(
                    snapshot => snapshot.ContainsText("+scroll-row-02 horizontal-preview-tail"),
                    TimeSpan.FromSeconds(5),
                    "History preview scrolls horizontally back to the left")
                .Build()
                .ApplyAsync(terminal, timeout.Token);

            await automator.ScrollDownAsync(10, timeout.Token);
            await new Hex1bTerminalInputSequenceBuilder()
                .MouseMoveTo(previewPoint.X, previewPoint.Y)
                .Shift()
                .ScrollDown(20)
                .Build()
                .ApplyAsync(terminal, timeout.Token);
            await automator.WaitUntilAsync(
                _ => previewEditor.ScrollOffset > 1 && previewEditor.HorizontalScrollOffset > 0,
                TimeSpan.FromSeconds(5),
                "History preview retains both scroll offsets before changing commits");
            Assert.IsGreaterThan(1, previewEditor.ScrollOffset);
            Assert.IsGreaterThan(0, previewEditor.HorizontalScrollOffset);
            var retainedVerticalOffset = previewEditor.ScrollOffset;
            var retainedHorizontalOffset = previewEditor.HorizontalScrollOffset;

            await session.LoadAsync(timeout.Token);

            Assert.AreEqual(retainedVerticalOffset, previewEditor.ScrollOffset);
            Assert.AreEqual(retainedHorizontalOffset, previewEditor.HorizontalScrollOffset);

            using (var restored = automator.CreateSnapshot())
            {
                var find = FindText(restored, "Find: ");
                await automator.ClickAtAsync(find.X + 6, find.Y, MouseButton.Left, timeout.Token);
            }

            var repaintCountBeforeFilter = Volatile.Read(ref cleanRepaintCount);
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
            Assert.IsFalse(filtered.ContainsText("stale-preview-tail-}],"));
            Assert.AreEqual(1, previewEditor.ScrollOffset);
            Assert.AreEqual(0, previewEditor.HorizontalScrollOffset);
            Assert.IsGreaterThan(
                repaintCountBeforeFilter + 1,
                Volatile.Read(ref cleanRepaintCount),
                "Changing commits must repaint once for selection and again when the delayed preview replaces the document.");
            Assert.AreEqual(
                " ",
                filtered.GetCell(staleTailEnd.X, staleTailEnd.Y).Character,
                "Switching to a shorter preview must clear the previous line through its final cell.");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies keyboard and wheel movement stop at both ends of the history list.
    /// </summary>
    [TestMethod]
    public async Task HistoryView_WithListNavigation_ClampsAtFirstAndLastCommit()
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
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
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
            using (var initial = automator.CreateSnapshot())
            {
                var firstRow = FindText(initial, "second commit");
                await automator.ClickAtAsync(
                    firstRow.X,
                    firstRow.Y,
                    MouseButton.Left,
                    timeout.Token);
            }

            await automator.KeyAsync(Hex1bKey.UpArrow, timeout.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            Assert.AreEqual(
                0,
                session.State.FocusedIndex,
                "Up Arrow must stop at the first history row.");

            await automator.ScrollUpAsync(1, timeout.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            Assert.AreEqual(
                0,
                session.State.FocusedIndex,
                "Wheel Up must stop at the first history row.");

            await automator.KeyAsync(Hex1bKey.DownArrow, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.FocusedIndex == session.State.VisibleItems.Length - 1,
                TimeSpan.FromSeconds(5),
                "Down Arrow focuses the last history row");
            await automator.KeyAsync(Hex1bKey.DownArrow, timeout.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            Assert.AreEqual(
                session.State.VisibleItems.Length - 1,
                session.State.FocusedIndex,
                "Down Arrow must stop at the last history row.");

            await automator.ScrollDownAsync(1, timeout.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            Assert.AreEqual(
                session.State.VisibleItems.Length - 1,
                session.State.FocusedIndex,
                "Wheel Down must stop at the last history row.");

            await automator.KeyAsync(Hex1bKey.UpArrow, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.FocusedIndex == 0,
                TimeSpan.FromSeconds(5),
                "Up Arrow returns to the first history row");
            await automator.ScrollUpAsync(1, timeout.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            Assert.AreEqual(
                0,
                session.State.FocusedIndex,
                "Returning to the first row must not roll over to the last row.");
        }
        finally
        {
            application?.RequestStop();
            await runTask;
            view.Detach();
        }
    }

    /// <summary>
    /// Verifies history identity, metadata, signature, and preview labels use the active UI locale.
    /// </summary>
    [TestMethod]
    public async Task HistoryView_WithJapaneseLocale_RendersLocalizedCommitDetails()
    {
        const string locale = "ja-JP";
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.GetCultureInfo(locale);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        try
        {
            using var session = await HistorySession.OpenAsync(
                CanonicalDirectory.Create(_temporaryDirectory!),
                new HistoryOptions(RevisionRange: null, Pathspecs: []),
                CreateProcessEnvironment(),
                TestContext.Current!.CancellationToken);
            await session.LoadAsync(TestContext.Current.CancellationToken);
            var focusedCommit = session.State.FocusedItem!.Commit;
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
                await automator.WaitUntilTextAsync(
                    AppMessages.HistoryPreviewCommitTitleForLocale(
                        locale,
                        focusedCommit.ObjectId.ToString()[..12]),
                    TimeSpan.FromSeconds(5));
                using var snapshot = automator.CreateSnapshot();
                Assert.IsTrue(snapshot.ContainsText(AppMessages.HistoryDetailAuthorForLocale(
                    locale,
                    author: "GitSail Test",
                    email: "gitsail@example.invalid")));
                Assert.IsTrue(snapshot.ContainsText(
                    AppMessages.HistoryDetailDateForLocale(locale, string.Empty)));
                Assert.IsTrue(snapshot.ContainsText(
                    AppMessages.HistoryDetailReferencesForLocale(locale, string.Empty)));
                Assert.IsTrue(snapshot.ContainsText(
                    AppMessages.HistoryDetailParentsForLocale(locale, string.Empty)));
                Assert.IsTrue(snapshot.ContainsText(AppMessages.HistoryDetailSignatureForLocale(
                    locale,
                    AppMessages.HistorySignatureUnsignedForLocale(locale))));
                Assert.IsFalse(snapshot.ContainsText("References:"));
                Assert.IsFalse(snapshot.ContainsText("Signature:"));
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
