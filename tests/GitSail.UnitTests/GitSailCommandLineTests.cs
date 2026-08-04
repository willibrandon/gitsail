using GitSail.CommandLine;
using GitSail.Ui;
using System.CommandLine;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies the System.CommandLine grammar and validation policy.
/// </summary>
[TestClass]
public sealed class GitSailCommandLineTests
{
    /// <summary>
    /// Verifies that the root command accepts an argument-free GUI invocation.
    /// </summary>
    [TestMethod]
    public void Parse_WithNoArguments_SelectsRootCommand()
    {
        var rootCommand = CreateRootCommand();

        var result = rootCommand.Parse([]);

        Assert.HasCount(0, result.Errors);
        Assert.AreSame(rootCommand, result.CommandResult.Command);
    }

    /// <summary>
    /// Verifies that each documented argument-free command is registered.
    /// </summary>
    /// <param name="command">The command name to parse.</param>
    [TestMethod]
    [DataRow("gui")]
    [DataRow("citool")]
    [DataRow("browser")]
    [DataRow("diff")]
    [DataRow("merge")]
    [DataRow("history")]
    [DataRow("rebase")]
    [DataRow("pick")]
    [DataRow("doctor")]
    [DataRow("help")]
    [DataRow("version")]
    public void Parse_WithDocumentedCommand_SelectsExpectedCommand(string command)
    {
        var rootCommand = CreateRootCommand();

        var result = rootCommand.Parse([command]);

        Assert.HasCount(0, result.Errors);
        Assert.AreEqual(command, result.CommandResult.Command.Name);
    }

    /// <summary>
    /// Verifies System.CommandLine forwards both interactive-rebase revision operands.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithRebaseOperands_ForwardsTypedRebaseOptions()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });

        var exitCode = await commandLine.CreateRootCommand()
            .Parse(["rebase", "--onto", "release/base", "topic~4"])
            .InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions);
        Assert.AreEqual(ApplicationMode.Rebase, observedOptions.Mode);
        Assert.IsNotNull(observedOptions.Rebase);
        Assert.AreEqual("topic~4", observedOptions.Rebase.Upstream);
        Assert.AreEqual("release/base", observedOptions.Rebase.Onto);
    }

    /// <summary>
    /// Verifies the Git-only sequence-editor command is hidden and System.CommandLine owns its path parsing.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithSequenceEditorPath_ForwardsExactHiddenCommandOperand()
    {
        string? observedPath = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            sequenceEditorRunner: (path, _) =>
            {
                observedPath = path;
                return Task.FromResult(ExitCodes.Success);
            });
        var root = commandLine.CreateRootCommand();
        var command = root.Subcommands.Single(static candidate => candidate.Name == "sequence-editor");

        var exitCode = await root.Parse(["sequence-editor", "/repo path/git-rebase-todo"]).InvokeAsync();

        Assert.IsTrue(command.Hidden);
        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.AreEqual("/repo path/git-rebase-todo", observedPath);
    }

    /// <summary>
    /// Verifies root help does not expose the Git-only sequence-editor callback command.
    /// </summary>
    [TestMethod]
    public void Invoke_WithRootHelp_OmitsHiddenSequenceEditorCommand()
    {
        using var output = new StringWriter();
        var configuration = new InvocationConfiguration
        {
            Output = output,
            Error = TextWriter.Null,
        };

        var exitCode = CreateRootCommand().Parse(["--help"]).Invoke(configuration);

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsFalse(output.ToString().Contains("sequence-editor", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that blame accepts its documented line, revision, separator, and path.
    /// </summary>
    [TestMethod]
    public void Parse_WithBlamePath_AcceptsDocumentedGrammar()
    {
        var rootCommand = CreateRootCommand();

        var result = rootCommand.Parse(["blame", "--line", "7", "HEAD", "--", "src/file name.cs"]);

        Assert.HasCount(0, result.Errors);
        Assert.AreEqual("blame", result.CommandResult.Command.Name);
    }

    /// <summary>
    /// Verifies System.CommandLine forwards blame revision, path, focus, range, and detection choices as typed options.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithBlameOperands_ForwardsTypedBlameOptions()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });

        var exitCode = await commandLine.CreateRootCommand().Parse(
            ["blame", "--line", "7", "--range", "3:12", "--detect-moves", "--detect-copies", "HEAD", "--", "src/file name.cs"])
            .InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions?.Blame);
        Assert.AreEqual(ApplicationMode.Blame, observedOptions.Mode);
        Assert.AreEqual("HEAD", observedOptions.Blame.Revision);
        Assert.HasCount(1, observedOptions.Blame.Paths);
        Assert.AreEqual("src/file name.cs", observedOptions.Blame.Paths[0]);
        Assert.AreEqual(7, observedOptions.Blame.Line);
        Assert.AreEqual("3:12", observedOptions.Blame.Range);
        Assert.IsTrue(observedOptions.Blame.DetectMoves);
        Assert.IsTrue(observedOptions.Blame.DetectCopies);
    }

    /// <summary>
    /// Verifies an operand after the option terminator is a blame path rather than a revision.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithBlamePathOnly_ForwardsPathWithoutRevision()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });

        var exitCode = await commandLine.CreateRootCommand()
            .Parse(["blame", "--", "src/file name.cs"])
            .InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions?.Blame);
        Assert.IsNull(observedOptions.Blame.Revision);
        Assert.HasCount(1, observedOptions.Blame.Paths);
        Assert.AreEqual("src/file name.cs", observedOptions.Blame.Paths[0]);
    }

    /// <summary>
    /// Verifies one unseparated blame operand is treated as the required file rather than an incomplete revision.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithSingleBlameOperand_ForwardsRequiredPath()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });

        var exitCode = await commandLine.CreateRootCommand()
            .Parse(["blame", "file.txt"])
            .InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions?.Blame);
        Assert.IsNull(observedOptions.Blame.Revision);
        Assert.HasCount(1, observedOptions.Blame.Paths);
        Assert.AreEqual("file.txt", observedOptions.Blame.Paths[0]);
    }

    /// <summary>
    /// Verifies blame rejects invocation without a direct or file-based path input.
    /// </summary>
    [TestMethod]
    public void Parse_WithoutBlamePath_ReturnsParseError()
    {
        var result = CreateRootCommand().Parse(["blame"]);

        Assert.HasCount(1, result.Errors);
        StringAssert.Contains(result.Errors[0].Message, "requires", StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies one blame operand remains a revision when the required path comes from a NUL-delimited file.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithBlamePathspecFile_ForwardsRevisionAndFileInput()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });

        var exitCode = await commandLine.CreateRootCommand().Parse(
            ["blame", "HEAD", "--pathspec-from-file", "paths.bin", "--pathspec-file-nul"])
            .InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions?.Blame);
        Assert.AreEqual("HEAD", observedOptions.Blame.Revision);
        Assert.IsEmpty(observedOptions.Blame.Paths);
        Assert.AreEqual("paths.bin", observedOptions.Blame.PathspecFile);
        Assert.IsTrue(observedOptions.Blame.PathspecFileNul);
    }

    /// <summary>
    /// Verifies malformed or descending blame ranges are rejected during parsing.
    /// </summary>
    /// <param name="range">The invalid range candidate.</param>
    [TestMethod]
    [DataRow("0:1")]
    [DataRow("2:1")]
    [DataRow("one:two")]
    [DataRow("1:")]
    public void Parse_WithInvalidBlameRange_ReturnsParseError(string range)
    {
        var result = CreateRootCommand().Parse(["blame", "--range", range, "--", "file.txt"]);

        Assert.HasCount(1, result.Errors);
        StringAssert.Contains(result.Errors[0].Message, "start:end", StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a requested initial line must be present in the requested blame range.
    /// </summary>
    [TestMethod]
    public void Parse_WithBlameLineOutsideRange_ReturnsParseError()
    {
        var result = CreateRootCommand().Parse(
            ["blame", "--line", "9", "--range", "2:5", "--", "file.txt"]);

        Assert.HasCount(1, result.Errors);
        StringAssert.Contains(result.Errors[0].Message, "inside", StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that System.CommandLine rejects an unknown command.
    /// </summary>
    [TestMethod]
    public void Parse_WithUnknownCommand_ReturnsParseError()
    {
        var rootCommand = CreateRootCommand();

        var result = rootCommand.Parse(["sail-away"]);

        Assert.HasCount(1, result.Errors);
        StringAssert.Contains(result.Errors[0].Message, "Unrecognized", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that the citool validator rejects mutually exclusive options.
    /// </summary>
    [TestMethod]
    public void Parse_WithConflictingCitoolOptions_ReturnsParseError()
    {
        var rootCommand = CreateRootCommand();

        var result = rootCommand.Parse(["citool", "--amend", "--nocommit"]);

        Assert.HasCount(1, result.Errors);
        StringAssert.Contains(result.Errors[0].Message, "mutually exclusive", StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies valid citool flags reach the interactive shell as typed single-transaction options.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithCitoolFlags_ForwardsCompleteShellOptions()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });
        var rootCommand = commandLine.CreateRootCommand();

        var exitCode = await rootCommand.Parse(["citool", "--amend", "--commitmsg"]).InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions);
        Assert.AreEqual(ApplicationMode.Citool, observedOptions.Mode);
        Assert.IsNotNull(observedOptions.Citool);
        Assert.IsTrue(observedOptions.Citool.Amend);
        Assert.IsFalse(observedOptions.Citool.NoCommit);
        Assert.IsTrue(observedOptions.Citool.OpenCommitMessage);
    }

    /// <summary>
    /// Verifies no-commit citool reaches the shell without implicitly enabling amend or message focus.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithNoCommit_ForwardsExclusiveCompletionMode()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Failure);
            });
        var rootCommand = commandLine.CreateRootCommand();

        var exitCode = await rootCommand.Parse(["citool", "--nocommit"]).InvokeAsync();

        Assert.AreEqual(ExitCodes.Failure, exitCode);
        Assert.IsNotNull(observedOptions?.Citool);
        Assert.IsFalse(observedOptions.Citool.Amend);
        Assert.IsTrue(observedOptions.Citool.NoCommit);
        Assert.IsFalse(observedOptions.Citool.OpenCommitMessage);
    }

    /// <summary>
    /// Verifies System.CommandLine forwards the history revision and path operands as typed shell options.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithHistoryOperands_ForwardsTypedHistoryOptions()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });
        var rootCommand = commandLine.CreateRootCommand();

        var exitCode = await rootCommand.Parse(
            ["history", "main..topic", "--", "src/file name.cs"]).InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions?.History);
        Assert.AreEqual(ApplicationMode.History, observedOptions.Mode);
        Assert.AreEqual("main..topic", observedOptions.History.RevisionRange);
        Assert.HasCount(1, observedOptions.History.Pathspecs);
        Assert.AreEqual("src/file name.cs", observedOptions.History.Pathspecs[0]);
    }

    /// <summary>
    /// Verifies a history operand following the option terminator is a pathspec rather than a revision.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithHistoryPathOnly_ForwardsPathWithoutRevision()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });

        var exitCode = await commandLine.CreateRootCommand()
            .Parse(["history", "--", "src/file name.cs"])
            .InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions?.History);
        Assert.IsNull(observedOptions.History.RevisionRange);
        Assert.HasCount(1, observedOptions.History.Pathspecs);
        Assert.AreEqual("src/file name.cs", observedOptions.History.Pathspecs[0]);
    }

    /// <summary>
    /// Verifies System.CommandLine forwards history pathspec-file options without reading them during parsing.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithHistoryPathspecFile_ForwardsTypedFileOptions()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });
        var rootCommand = commandLine.CreateRootCommand();

        var exitCode = await rootCommand.Parse(
            ["history", "--pathspec-from-file", "paths.bin", "--pathspec-file-nul"]).InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions?.History);
        Assert.AreEqual("paths.bin", observedOptions.History.PathspecFile);
        Assert.IsTrue(observedOptions.History.PathspecFileNul);
    }

    /// <summary>
    /// Verifies System.CommandLine forwards browser revision, directory, and pathspec-file operands.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithBrowserOperands_ForwardsTypedBrowserOptions()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });
        var rootCommand = commandLine.CreateRootCommand();

        var exitCode = await rootCommand.Parse(
            ["browser", "main", "src", "--pathspec-from-file", "more.bin", "--pathspec-file-nul"])
            .InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions?.Browser);
        Assert.AreEqual(ApplicationMode.Browser, observedOptions.Mode);
        Assert.AreEqual("main", observedOptions.Browser.Revision);
        Assert.HasCount(1, observedOptions.Browser.Directories);
        Assert.AreEqual("src", observedOptions.Browser.Directories[0]);
        Assert.AreEqual("more.bin", observedOptions.Browser.PathspecFile);
        Assert.IsTrue(observedOptions.Browser.PathspecFileNul);
    }

    /// <summary>
    /// Verifies a browser operand following the option terminator is a directory rather than a revision.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithBrowserDirectoryOnly_ForwardsDirectoryWithoutRevision()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });

        var exitCode = await commandLine.CreateRootCommand()
            .Parse(["browser", "--", "src tree"])
            .InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions?.Browser);
        Assert.IsNull(observedOptions.Browser.Revision);
        Assert.HasCount(1, observedOptions.Browser.Directories);
        Assert.AreEqual("src tree", observedOptions.Browser.Directories[0]);
    }

    /// <summary>
    /// Verifies System.CommandLine rejects more than one browser directory operand.
    /// </summary>
    [TestMethod]
    public void Parse_WithTooManyBrowserDirectories_ReturnsParseError()
    {
        var result = CreateRootCommand().Parse(["browser", "HEAD", "first", "second"]);

        Assert.HasCount(1, result.Errors);
        StringAssert.Contains(result.Errors[0].Message, "Unrecognized", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies System.CommandLine forwards both revisions and terminated pathspecs to diff mode.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithDiffPairAndPaths_ForwardsTypedDiffOptions()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });

        var exitCode = await commandLine.CreateRootCommand().Parse(
            ["diff", "main~1", "main", "--", "src/file name.cs", "README.md"])
            .InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions?.Diff);
        Assert.AreEqual(ApplicationMode.Diff, observedOptions.Mode);
        Assert.IsFalse(observedOptions.Diff.Cached);
        Assert.AreEqual("main~1", observedOptions.Diff.LeftRevision);
        Assert.AreEqual("main", observedOptions.Diff.RightRevision);
        Assert.HasCount(2, observedOptions.Diff.Pathspecs);
        Assert.AreEqual("src/file name.cs", observedOptions.Diff.Pathspecs[0]);
        Assert.AreEqual("README.md", observedOptions.Diff.Pathspecs[1]);
    }

    /// <summary>
    /// Verifies cached diff forwards its single base revision and pathspec-file inputs.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithCachedDiff_ForwardsSingleRevisionAndFileInput()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });

        var exitCode = await commandLine.CreateRootCommand().Parse(
            ["diff", "--cached", "HEAD~2", "--pathspec-from-file", "paths.bin", "--pathspec-file-nul"])
            .InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions?.Diff);
        Assert.IsTrue(observedOptions.Diff.Cached);
        Assert.AreEqual("HEAD~2", observedOptions.Diff.LeftRevision);
        Assert.IsNull(observedOptions.Diff.RightRevision);
        Assert.AreEqual("paths.bin", observedOptions.Diff.PathspecFile);
        Assert.IsTrue(observedOptions.Diff.PathspecFileNul);
    }

    /// <summary>
    /// Verifies a diff operand after the option terminator is a pathspec rather than a revision.
    /// </summary>
    [TestMethod]
    public async Task InvokeAsync_WithDiffPathOnly_ForwardsPathWithoutRevision()
    {
        GitSailShellOptions? observedOptions = null;
        var commandLine = new GitSailCommandLine(
            CancellationToken.None,
            (options, _) =>
            {
                observedOptions = options;
                return Task.FromResult(ExitCodes.Success);
            });

        var exitCode = await commandLine.CreateRootCommand()
            .Parse(["diff", "--", "src/file name.cs"])
            .InvokeAsync();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.IsNotNull(observedOptions?.Diff);
        Assert.IsNull(observedOptions.Diff.LeftRevision);
        Assert.IsNull(observedOptions.Diff.RightRevision);
        Assert.HasCount(1, observedOptions.Diff.Pathspecs);
        Assert.AreEqual("src/file name.cs", observedOptions.Diff.Pathspecs[0]);
    }

    /// <summary>
    /// Verifies that completion accepts each supported shell.
    /// </summary>
    /// <param name="shell">The supported shell name.</param>
    [TestMethod]
    [DataRow("bash")]
    [DataRow("zsh")]
    [DataRow("fish")]
    [DataRow("powershell")]
    public void Parse_WithCompletionShell_AcceptsSupportedShell(string shell)
    {
        var rootCommand = CreateRootCommand();

        var result = rootCommand.Parse(["completion", shell]);

        Assert.HasCount(0, result.Errors);
    }

    /// <summary>
    /// Verifies that the built-in version option writes stable GitSail product identity.
    /// </summary>
    [TestMethod]
    public void Invoke_WithVersionOption_WritesGitSailVersion()
    {
        var rootCommand = CreateRootCommand();
        using var output = new StringWriter();
        var configuration = new InvocationConfiguration
        {
            Output = output,
            Error = TextWriter.Null,
        };

        var exitCode = rootCommand.Parse(["--version"]).Invoke(configuration);

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.AreEqual(BuildInformation.DisplayVersion + Environment.NewLine, output.ToString());
    }

    private static RootCommand CreateRootCommand()
        => new GitSailCommandLine(CancellationToken.None).CreateRootCommand();
}
