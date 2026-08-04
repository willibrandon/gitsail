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
/// Verifies immutable tree browsing and pointer interaction against isolated real Git repositories.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TreeServiceTests
{
    private string? _temporaryDirectory;
    private GitInstallation? _installation;
    private ChildProcessRunner? _runner;
    private TreeService? _service;

    /// <summary>
    /// Creates an isolated repository containing every supported tree entry kind.
    /// </summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _runner = new ChildProcessRunner();
        var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
        _installation = await new GitVersionService(resolver, _runner).GetAsync(
            CanonicalDirectory.Create(_temporaryDirectory),
            TestContext.Current!.CancellationToken);
        _service = new TreeService(
            _installation,
            _runner,
            TestProcessEnvironment.CreateGitFactory(_temporaryDirectory));
        await RunGitAsync(StandardInputSource.Empty(), "init", "--quiet", "--initial-branch=main");
        Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "nested"));
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "root.txt"),
            "root content\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "nested", "child.txt"),
            "child content\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_temporaryDirectory, "script.sh"),
            "#!/bin/sh\necho tree\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync(StandardInputSource.Empty(), "add", "--", "root.txt", "nested/child.txt", "script.sh");
        await RunGitAsync(StandardInputSource.Empty(), "update-index", "--chmod=+x", "script.sh");
        var linkObject = await RunGitForOutputAsync(
            StandardInputSource.FromBytes("target"u8),
            "hash-object",
            "-w",
            "--stdin");
        await RunGitAsync(
            StandardInputSource.Empty(),
            "update-index",
            "--add",
            "--cacheinfo",
            $"120000,{linkObject},link");
        await RunGitAsync(
            StandardInputSource.Empty(),
            "commit",
            "--quiet",
            "--no-gpg-sign",
            "--message=tree fixture");
        var commit = await RunGitForOutputAsync(StandardInputSource.Empty(), "rev-parse", "HEAD");
        await RunGitAsync(
            StandardInputSource.Empty(),
            "update-index",
            "--add",
            "--cacheinfo",
            $"160000,{commit},module");
        await RunGitAsync(
            StandardInputSource.Empty(),
            "commit",
            "--quiet",
            "--no-gpg-sign",
            "--message=add gitlink");
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
    /// Verifies root browsing returns every exact supported tree kind and canonical mode.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_AtRepositoryRoot_ReturnsEveryTreeKind()
    {
        var catalog = await _service!.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            Revision.Create("HEAD"),
            directory: null,
            TestContext.Current!.CancellationToken);

        Assert.HasCount(5, catalog.Entries);
        TestSeq.AreEqual(
            new[]
            {
                TreeEntryKind.SymbolicLink,
                TreeEntryKind.GitLink,
                TreeEntryKind.Tree,
                TreeEntryKind.RegularFile,
                TreeEntryKind.ExecutableFile,
            },
            catalog.Entries.Select(static entry => entry.Kind));
        Assert.IsNull(catalog.Directory);
        Assert.AreEqual(RepositoryObjectFormat.Sha1, catalog.TreeObjectId.Format);
    }

    /// <summary>
    /// Verifies a starting directory is resolved exactly and listed lazily from its tree object.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithDirectory_ReturnsImmediateChildren()
    {
        var directory = CreatePath("nested");

        var catalog = await _service!.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            Revision.Create("HEAD"),
            directory,
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(directory, catalog.Directory);
        var child = TestSeq.Single(catalog.Entries);
        Assert.AreEqual(TreeEntryKind.RegularFile, child.Kind);
        Assert.AreEqual("child.txt", child.Name.DisplayText);
    }

    /// <summary>
    /// Verifies selecting a blob as the starting directory fails without listing another tree.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithFileInsteadOfDirectory_ThrowsGitCommandException()
    {
        await Assert.ThrowsAsync<GitCommandException>(() => _service!.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            Revision.Create("HEAD"),
            CreatePath("root.txt"),
            TestContext.Current!.CancellationToken));
    }

    /// <summary>
    /// Verifies an invalid revision fails through Git's literal revision validation.
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_WithInvalidRevision_ThrowsGitCommandException()
    {
        await Assert.ThrowsAsync<GitCommandException>(() => _service!.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            Revision.Create("missing-revision"),
            directory: null,
            TestContext.Current!.CancellationToken));
    }

    /// <summary>
    /// Verifies a NUL-delimited directory file supplies the exact initial browser directory.
    /// </summary>
    [TestMethod]
    public async Task TreeSession_WithPathspecFile_LoadsExactDirectory()
    {
        var inputFile = Path.Combine(_temporaryDirectory!, "browser-path.bin");
        await File.WriteAllBytesAsync(
            inputFile,
            [(byte)'n', (byte)'e', (byte)'s', (byte)'t', (byte)'e', (byte)'d', 0],
            TestContext.Current!.CancellationToken);
        var session = await TreeSession.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            new BrowserOptions(
                Revision: "HEAD",
                Directories: [],
                PathspecFile: inputFile,
                PathspecFileNul: true),
            CreateProcessEnvironment(),
            TestContext.Current.CancellationToken);

        await session.LoadRevisionAsync(TestContext.Current.CancellationToken);

        Assert.IsFalse(session.HasLoadFailure, session.Activity);
        Assert.AreEqual("nested", session.State.Catalog!.Directory!.DisplayText);
        Assert.AreEqual("child.txt", TestSeq.Single(session.State.Catalog.Entries).Name.DisplayText);
    }

    /// <summary>
    /// Verifies regular files and symbolic links retain exact blob bytes through spillable capture.
    /// </summary>
    [TestMethod]
    public async Task ReadBlobAsync_WithFileAndLink_ReturnsExactContent()
    {
        var catalog = await _service!.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            Revision.Create("HEAD"),
            directory: null,
            TestContext.Current!.CancellationToken);
        var rootFile = catalog.Entries.Single(static entry => entry.Name.DisplayText == "root.txt");
        var link = catalog.Entries.Single(static entry => entry.Name.DisplayText == "link");

        using var fileSpool = await _service.ReadBlobAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            rootFile,
            TestContext.Current.CancellationToken);
        using var linkSpool = await _service.ReadBlobAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            link,
            TestContext.Current.CancellationToken);

        Assert.AreEqual("root content\n", Encoding.UTF8.GetString(await fileSpool.ReadSliceAsync(
            0,
            (int)fileSpool.Length,
            TestContext.Current.CancellationToken)));
        Assert.AreEqual("target", Encoding.UTF8.GetString(await linkSpool.ReadSliceAsync(
            0,
            (int)linkSpool.Length,
            TestContext.Current.CancellationToken)));
    }

    /// <summary>
    /// Verifies responsive mouse selection, directory navigation, search, and exact preview rendering.
    /// </summary>
    /// <param name="width">The terminal width under test.</param>
    /// <param name="height">The terminal height under test.</param>
    [TestMethod]
    [DataRow(80, 24)]
    [DataRow(120, 30)]
    public async Task TreeView_WithKeyboardAndMouse_NavigatesAndFiltersExactEntries(int width, int height)
    {
        var session = await TreeSession.OpenAsync(
            CanonicalDirectory.Create(_temporaryDirectory!),
            new BrowserOptions(Revision: "HEAD", Directories: []),
            CreateProcessEnvironment(),
            TestContext.Current!.CancellationToken);
        await session.LoadRevisionAsync(TestContext.Current.CancellationToken);
        var view = new TreeView(session, TestContext.Current.CancellationToken);
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
            await automator.WaitUntilTextAsync("nested", TimeSpan.FromSeconds(5));
            using (var root = automator.CreateSnapshot())
            {
                Assert.IsTrue(root.ContainsText("root.txt"));
                Assert.IsTrue(root.ContainsText("script.sh"));
                Assert.IsTrue(root.ContainsText("Mouse Select/Open"));
                var header = root.GetLine(0);
                StringAssert.Contains(header, "repository root");
                StringAssert.Contains(header, " | gitsail-tree-");
                StringAssert.Contains(header, Path.GetFileName(_temporaryDirectory!)![..12]);
                Assert.IsFalse(header.Contains(_temporaryDirectory!, StringComparison.Ordinal));
                var nested = FindText(root, "nested");
                await automator.ClickAtAsync(nested.X + 1, nested.Y, MouseButton.Left, timeout.Token);
            }

            await automator.WaitUntilAsync(
                _ => session.State.FocusedItem?.Entry.Kind == TreeEntryKind.Tree,
                TimeSpan.FromSeconds(5),
                "Mouse selection focuses the exact tree entry");
            await automator.WaitUntilAsync(
                _ => application?.FocusedNode is ListNode<TreeWorkspaceItem>,
                TimeSpan.FromSeconds(5),
                "Mouse selection focuses the tree list");
            ButtonNode? openButton = null;
            await automator.WaitUntilAsync(
                _ => (openButton = application?.Focusables
                    .OfType<ButtonNode>()
                    .SingleOrDefault(static button => string.Equals(button.Label, "Open", StringComparison.Ordinal))) is not null,
                TimeSpan.FromSeconds(5),
                "The tree Open button is arranged and focusable");
            var openBounds = openButton!.HitTestBounds;
            await automator.ClickAtAsync(
                openBounds.X + (openBounds.Width / 2),
                openBounds.Y,
                MouseButton.Left,
                timeout.Token);

            await automator.WaitUntilTextAsync("child.txt", TimeSpan.FromSeconds(5));
            Assert.AreEqual("nested", session.State.Catalog!.Directory!.DisplayText);
            await automator.WaitUntilAsync(
                _ => application?.FocusedNode is ListNode<TreeWorkspaceItem>,
                TimeSpan.FromSeconds(5),
                "Opening a directory keeps focus in the tree list");
            await automator.KeyAsync(Hex1bKey.Backspace, timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.Catalog?.Directory is null,
                TimeSpan.FromSeconds(5),
                "Backspace returns to the repository root");
            using (var parent = automator.CreateSnapshot())
            {
                var find = FindText(parent, "Find: ");
                await automator.ClickAtAsync(find.X + 6, find.Y, MouseButton.Left, timeout.Token);
            }

            await automator.TypeAsync("root.txt", timeout.Token);
            await automator.WaitUntilAsync(
                _ => session.State.VisibleItems.Length == 1 &&
                    session.State.FocusedItem?.Entry.Name.DisplayText == "root.txt",
                TimeSpan.FromSeconds(5),
                "Tree search focuses the only exact matching entry");
            await automator.WaitUntilTextAsync("root content", TimeSpan.FromSeconds(5));
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

    private async Task RunGitAsync(StandardInputSource input, params string[] arguments)
    {
        var result = await RunGitCoreAsync(input, arguments);
        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
    }

    private async Task<string> RunGitForOutputAsync(StandardInputSource input, params string[] arguments)
    {
        var result = await RunGitCoreAsync(input, arguments);
        Assert.AreEqual(0, result.ExitCode, Encoding.UTF8.GetString(result.StandardError.Span));
        return Encoding.UTF8.GetString(result.StandardOutput.Span).Trim();
    }

    private Task<ProcessResult> RunGitCoreAsync(StandardInputSource input, string[] arguments)
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
            input,
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));
        return _runner!.RunAsync(invocation, TestContext.Current!.CancellationToken);
    }

    private static GitPath CreatePath(string value)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(value)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(value));

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
