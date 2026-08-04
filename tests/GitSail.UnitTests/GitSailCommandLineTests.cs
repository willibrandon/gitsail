using GitSail.CommandLine;
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
