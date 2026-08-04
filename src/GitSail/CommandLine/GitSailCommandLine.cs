using GitSail.Features.Doctor;
using GitSail.Git.Execution;
using GitSail.Ui;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Completions;

namespace GitSail.CommandLine;

/// <summary>
/// Builds the complete System.CommandLine command, option, help, completion, and action model.
/// </summary>
internal sealed class GitSailCommandLine
{
    private static readonly string[] s_completionShells = ["bash", "zsh", "fish", "powershell"];
    private static readonly string[] s_rootHelpArguments = ["--help"];
    private readonly CancellationToken _cancellationToken;
    private readonly Func<GitSailShellOptions, CancellationToken, Task<int>>? _shellRunner;

    /// <summary>
    /// Initializes the command model for one process invocation.
    /// </summary>
    /// <param name="cancellationToken">Signals cancellation to invoked commands.</param>
    /// <param name="shellRunner">The optional interactive-shell test seam.</param>
    internal GitSailCommandLine(
        CancellationToken cancellationToken,
        Func<GitSailShellOptions, CancellationToken, Task<int>>? shellRunner = null)
    {
        _cancellationToken = cancellationToken;
        _shellRunner = shellRunner;
    }

    /// <summary>
    /// Creates the complete root command and every documented subcommand.
    /// </summary>
    /// <returns>The configured root command.</returns>
    internal RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("A cross-platform Git client with first-class keyboard and mouse support.")
        {
            TreatUnmatchedTokensAsErrors = true,
        };

        var rootWorkingDirectoryOption = CreateWorkingDirectoryOption();
        var rootTraceOption = CreateTraceOption();
        rootCommand.Options.Add(rootWorkingDirectoryOption);
        rootCommand.Options.Add(rootTraceOption);
        var versionOption = rootCommand.Options.OfType<VersionOption>().Single();
        versionOption.Action = new ProductVersionAction();
        rootCommand.SetAction((parseResult, _) => RunShellAsync(
            ApplicationMode.Gui,
            parseResult.GetValue(rootWorkingDirectoryOption)));

        rootCommand.Subcommands.Add(CreateGuiCommand());
        rootCommand.Subcommands.Add(CreateCitoolCommand());
        rootCommand.Subcommands.Add(CreateBlameCommand());
        rootCommand.Subcommands.Add(CreateBrowserCommand());
        rootCommand.Subcommands.Add(CreateDiffCommand());
        rootCommand.Subcommands.Add(CreateMergeCommand());
        rootCommand.Subcommands.Add(CreateHistoryCommand());
        rootCommand.Subcommands.Add(CreateRebaseCommand());
        rootCommand.Subcommands.Add(CreateInteractiveCommand("pick", "Choose a repository.", ApplicationMode.Pick));
        rootCommand.Subcommands.Add(CreateDoctorCommand());
        rootCommand.Subcommands.Add(CreateHelpCommand(rootCommand));
        rootCommand.Subcommands.Add(CreateCompletionCommand(rootCommand));
        rootCommand.Subcommands.Add(CreateVersionCommand());
        return rootCommand;
    }

    private Command CreateGuiCommand()
    {
        var command = new Command("gui", "Open the commit workspace.");
        var workingDirectoryOption = CreateWorkingDirectoryOption();
        command.Options.Add(workingDirectoryOption);
        command.Options.Add(CreateTraceOption());
        command.SetAction((parseResult, _) => RunShellAsync(
            ApplicationMode.Gui,
            parseResult.GetValue(workingDirectoryOption)));
        return command;
    }

    private Command CreateCitoolCommand()
    {
        var amendOption = new Option<bool>("--amend") { Description = "Amend the current HEAD commit." };
        var noCommitOption = new Option<bool>("--nocommit") { Description = "Prepare the commit without completing it." };
        var commitMessageOption = new Option<bool>("--commitmsg") { Description = "Open the commit-message workflow." };
        var command = new Command("citool", "Complete one commit transaction.");
        command.Options.Add(amendOption);
        command.Options.Add(noCommitOption);
        command.Options.Add(commitMessageOption);
        command.Validators.Add(result =>
        {
            if (result.GetValue(amendOption) && result.GetValue(noCommitOption))
            {
                result.AddError("Options '--amend' and '--nocommit' are mutually exclusive.");
            }
        });
        command.SetAction((parseResult, _) => RunShellAsync(
            ApplicationMode.Citool,
            workingDirectory: null,
            citool: new CitoolOptions(
                parseResult.GetValue(amendOption),
                parseResult.GetValue(noCommitOption),
                parseResult.GetValue(commitMessageOption))));
        return command;
    }

    private Command CreateBlameCommand()
    {
        var command = CreateInteractiveCommand("blame", "Inspect line history for a file.", ApplicationMode.Blame);
        var lineOption = new Option<int?>("--line") { Description = "Focus the specified one-based line number.", HelpName = "number" };
        lineOption.Validators.Add(static result =>
        {
            var value = result.GetValueOrDefault<int?>();
            if (value is <= 0)
            {
                result.AddError("Option '--line' requires a positive line number.");
            }
        });
        command.Options.Add(lineOption);
        AddPathspecOptions(command);
        AddFlexibleArguments(command, "revision-and-path");
        return command;
    }

    private Command CreateBrowserCommand()
    {
        var command = CreateInteractiveCommand("browser", "Browse a tree at a revision.", ApplicationMode.Browser);
        AddPathspecOptions(command);
        AddFlexibleArguments(command, "revision-and-directory");
        return command;
    }

    private Command CreateDiffCommand()
    {
        var command = CreateInteractiveCommand("diff", "Compare worktree, index, or revisions.", ApplicationMode.Diff);
        command.Options.Add(new Option<bool>("--cached") { Description = "Compare staged changes." });
        AddPathspecOptions(command);
        AddFlexibleArguments(command, "revisions-and-pathspecs");
        return command;
    }

    private Command CreateMergeCommand()
    {
        var command = CreateInteractiveCommand("merge", "Resolve unmerged paths.", ApplicationMode.Merge);
        AddPathspecOptions(command);
        AddFlexibleArguments(command, "paths");
        return command;
    }

    private Command CreateHistoryCommand()
    {
        var revisionRangeArgument = new Argument<string?>("revision-range")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The literal revision range to browse.",
        };
        var pathspecArgument = new Argument<string[]>("pathspec")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "The pathspecs used to restrict commit history.",
        };
        var command = new Command("history", "Browse structured commit history.")
        {
            revisionRangeArgument,
            pathspecArgument,
        };
        var pathspecOptions = AddPathspecOptions(command);
        command.SetAction((parseResult, _) => RunShellAsync(
            ApplicationMode.History,
            workingDirectory: null,
            history: new HistoryOptions(
                parseResult.GetValue(revisionRangeArgument),
                parseResult.GetValue(pathspecArgument)?.ToImmutableArray() ?? [],
                parseResult.GetValue(pathspecOptions.FromFile),
                parseResult.GetValue(pathspecOptions.FileNul))));
        return command;
    }

    private Command CreateRebaseCommand()
    {
        var command = CreateInteractiveCommand("rebase", "Plan or continue an interactive rebase.", ApplicationMode.Rebase);
        command.Options.Add(new Option<string?>("--onto") { Description = "Rebase onto the specified revision.", HelpName = "revision" });
        var upstreamArgument = new Argument<string?>("upstream")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The upstream revision.",
        };
        command.Arguments.Add(upstreamArgument);
        return command;
    }

    private Command CreateDoctorCommand()
    {
        var jsonOption = new Option<bool>("--json") { Description = "Write the stable machine-readable report." };
        var command = new Command("doctor", "Inspect installation and runtime capabilities.") { jsonOption };
        command.SetAction((parseResult, _) => WriteDoctorAsync(parseResult.GetValue(jsonOption)));
        return command;
    }

    private async Task<int> WriteDoctorAsync(bool json)
    {
        GitInstallation? installation = null;
        string? error = null;
        try
        {
            var resolver = new ExecutableResolver(new RuntimeProcessEnvironment());
            var service = new GitVersionService(resolver, new ChildProcessRunner());
            installation = await service.GetAsync(
                CanonicalDirectory.Create(Environment.CurrentDirectory),
                _cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ExecutableResolutionException or
            GitCommandException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
        }

        DoctorReportWriter.Write(json, installation, error);
        return installation is null ? ExitCodes.Failure : ExitCodes.Success;
    }

    private static Command CreateHelpCommand(RootCommand rootCommand)
    {
        var topicArgument = new Argument<string?>("command")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The command whose help should be displayed.",
        };
        topicArgument.CompletionSources.Add(_ => rootCommand.Subcommands
            .Select(static command => new CompletionItem(command.Name))
            .ToArray());
        var command = new Command("help", "Show the embedded offline command manual.") { topicArgument };
        command.SetAction(parseResult =>
        {
            var topic = parseResult.GetValue(topicArgument);
            var helpArguments = topic is null ? s_rootHelpArguments : new[] { topic, "--help" };
            return rootCommand.Parse(helpArguments).Invoke();
        });
        return command;
    }

    private static Command CreateCompletionCommand(RootCommand rootCommand)
    {
        var shellArgument = new Argument<string>("shell")
        {
            Arity = ArgumentArity.ExactlyOne,
            Description = "The target shell.",
        };
        shellArgument.CompletionSources.Add(_ => s_completionShells
            .Select(static shell => new CompletionItem(shell))
            .ToArray());
        shellArgument.Validators.Add(static result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (!s_completionShells.Contains(value, StringComparer.Ordinal))
            {
                result.AddError($"Unsupported completion shell '{value}'.");
            }
        });
        var command = new Command("completion", "Generate a shell completion script.") { shellArgument };
        command.SetAction(parseResult =>
        {
            CompletionRenderer.Write(rootCommand, parseResult.GetValue(shellArgument)!, Console.Out);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateVersionCommand()
    {
        var command = new Command("version", "Print the GitSail version.");
        command.SetAction(_ => WriteVersionAsync());
        return command;
    }

    private Command CreateInteractiveCommand(string name, string description, ApplicationMode mode)
    {
        var command = new Command(name, description);
        command.SetAction((_, _) => RunShellAsync(mode));
        return command;
    }

    private Task<int> RunShellAsync(ApplicationMode mode)
        => RunShellAsync(mode, workingDirectory: null);

    private Task<int> RunShellAsync(
        ApplicationMode mode,
        string? workingDirectory,
        CitoolOptions? citool = null,
        HistoryOptions? history = null)
    {
        var options = new GitSailShellOptions(mode, workingDirectory, citool, history);
        if (_shellRunner is not null)
        {
            return _shellRunner(options, _cancellationToken);
        }

        var shell = new GitSailShell(options);
        return RunShellCoreAsync(shell);
    }

    private async Task<int> RunShellCoreAsync(GitSailShell shell)
        => await shell.RunAsync(_cancellationToken).ConfigureAwait(false);

    private static async Task<int> WriteVersionAsync()
    {
        await Console.Out.WriteLineAsync(BuildInformation.DisplayVersion).ConfigureAwait(false);
        return ExitCodes.Success;
    }

    private static Option<string?> CreateWorkingDirectoryOption()
        => new("--working-dir")
        {
            Description = "Open the repository containing this directory.",
            HelpName = "directory",
        };

    private static Option<string?> CreateTraceOption()
        => new("--trace")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Write structured trace output, optionally to a selected file.",
            HelpName = "file",
        };

    private static (Option<string?> FromFile, Option<bool> FileNul) AddPathspecOptions(Command command)
    {
        var pathspecFromFileOption = new Option<string?>("--pathspec-from-file")
        {
            Description = "Read pathspec records from a file or standard input.",
            HelpName = "file|-",
        };
        var pathspecFileNulOption = new Option<bool>("--pathspec-file-nul")
        {
            Description = "Require NUL-delimited pathspec records.",
        };
        command.Options.Add(pathspecFromFileOption);
        command.Options.Add(pathspecFileNulOption);
        command.Validators.Add(result =>
        {
            if (result.GetValue(pathspecFileNulOption) && result.GetValue(pathspecFromFileOption) is null)
            {
                result.AddError("Option '--pathspec-file-nul' requires '--pathspec-from-file'.");
            }
        });
        return (pathspecFromFileOption, pathspecFileNulOption);
    }

    private static void AddFlexibleArguments(Command command, string name)
    {
        var arguments = new Argument<string[]>(name)
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Revision and path operands separated according to the command usage.",
        };
        command.Arguments.Add(arguments);
    }
}
