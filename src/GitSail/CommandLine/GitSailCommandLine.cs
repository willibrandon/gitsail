using GitSail.Diagnostics;
using GitSail.Domain;
using GitSail.Features.Doctor;
using GitSail.Features.Help;
using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using GitSail.Ui;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Parsing;

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
    private readonly Func<GitPath, CancellationToken, Task<int>>? _sequenceEditorRunner;
    private readonly ImmutableArray<GitPath>? _nativePathsAfterDoubleDash;

    /// <summary>
    /// Initializes the command model for one process invocation.
    /// </summary>
    /// <param name="cancellationToken">Signals cancellation to invoked commands.</param>
    /// <param name="shellRunner">The optional interactive-shell test seam.</param>
    /// <param name="sequenceEditorRunner">The optional sequence-editor test seam.</param>
    /// <param name="nativePathsAfterDoubleDash">The exact process paths following an option terminator.</param>
    internal GitSailCommandLine(
        CancellationToken cancellationToken,
        Func<GitSailShellOptions, CancellationToken, Task<int>>? shellRunner = null,
        Func<GitPath, CancellationToken, Task<int>>? sequenceEditorRunner = null,
        ImmutableArray<GitPath>? nativePathsAfterDoubleDash = null)
    {
        _cancellationToken = cancellationToken;
        _shellRunner = shellRunner;
        _sequenceEditorRunner = sequenceEditorRunner;
        _nativePathsAfterDoubleDash = nativePathsAfterDoubleDash;
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
            parseResult.GetValue(rootWorkingDirectoryOption),
            trace: BindTraceOptions(parseResult, rootTraceOption)));

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
        rootCommand.Subcommands.Add(CreateSequenceEditorCommand());
        rootCommand.Subcommands.Add(CreateCompletionCandidatesCommand(rootCommand));
        return rootCommand;
    }

    private Command CreateGuiCommand()
    {
        var command = new Command("gui", "Open the commit workspace.");
        var workingDirectoryOption = CreateWorkingDirectoryOption();
        var traceOption = CreateTraceOption();
        command.Options.Add(workingDirectoryOption);
        command.Options.Add(traceOption);
        command.SetAction((parseResult, _) => RunShellAsync(
            ApplicationMode.Gui,
            parseResult.GetValue(workingDirectoryOption),
            trace: BindTraceOptions(parseResult, traceOption)));
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
        var revisionArgument = new Argument<string?>("revision")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The literal revision whose file should be inspected.",
        };
        var pathArgument = new Argument<string?>("path")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The repository-relative file to inspect.",
        };
        var command = new Command("blame", "Inspect line history for a file.")
        {
            revisionArgument,
            pathArgument,
        };
        var lineOption = new Option<int?>("--line") { Description = "Focus the specified one-based line number.", HelpName = "number" };
        var rangeOption = new Option<string?>("--range")
        {
            Description = "Load an inclusive one-based line range in start:end form.",
            HelpName = "start:end",
        };
        var detectMovesOption = new Option<bool>("--detect-moves")
        {
            Description = "Detect moved lines within the selected file.",
        };
        var detectCopiesOption = new Option<bool>("--detect-copies")
        {
            Description = "Detect lines copied from other files.",
        };
        lineOption.Validators.Add(static result =>
        {
            var value = result.GetValueOrDefault<int?>();
            if (value is <= 0)
            {
                result.AddError("Option '--line' requires a positive line number.");
            }
        });
        rangeOption.Validators.Add(static result =>
        {
            var value = result.GetValueOrDefault<string?>();
            if (value is not null && !Domain.BlameRange.TryParse(value, out _))
            {
                result.AddError("Option '--range' requires start:end with positive line numbers and start no greater than end.");
            }
        });
        command.Options.Add(lineOption);
        command.Options.Add(rangeOption);
        command.Options.Add(detectMovesOption);
        command.Options.Add(detectCopiesOption);
        var pathspecOptions = AddPathspecOptions(command);
        command.Validators.Add(result =>
        {
            var line = result.GetValue(lineOption);
            var rangeText = result.GetValue(rangeOption);
            if (line is not null && rangeText is not null &&
                Domain.BlameRange.TryParse(rangeText, out var range) &&
                (line < range!.Start || line > range.End))
            {
                result.AddError("Option '--line' must focus a line inside '--range'.");
            }

            var revision = result.GetValue(revisionArgument);
            var path = result.GetValue(pathArgument);
            var pathspecFile = result.GetValue(pathspecOptions.FromFile);
            if (revision is null && path is null && pathspecFile is null)
            {
                result.AddError("Command 'blame' requires exactly one file path or '--pathspec-from-file'.");
            }

            if (path is not null && pathspecFile is not null)
            {
                result.AddError("A direct blame path and '--pathspec-from-file' cannot be used together.");
            }
        });
        command.SetAction((parseResult, _) => RunShellAsync(
            ApplicationMode.Blame,
            workingDirectory: null,
            blame: BindBlameOptions(
                parseResult,
                revisionArgument,
                pathArgument,
                lineOption,
                rangeOption,
                detectMovesOption,
                detectCopiesOption,
                pathspecOptions,
                _nativePathsAfterDoubleDash)));
        return command;
    }

    private Command CreateBrowserCommand()
    {
        var revisionArgument = new Argument<string?>("revision")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The literal revision to browse.",
        };
        var directoryArgument = new Argument<string?>("directory")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The optional repository-relative starting directory.",
        };
        var command = new Command("browser", "Browse a tree at a revision.")
        {
            revisionArgument,
            directoryArgument,
        };
        var pathspecOptions = AddPathspecOptions(command);
        command.SetAction((parseResult, _) => RunShellAsync(
            ApplicationMode.Browser,
            workingDirectory: null,
            browser: BindBrowserOptions(
                parseResult,
                revisionArgument,
                directoryArgument,
                pathspecOptions,
                _nativePathsAfterDoubleDash)));
        return command;
    }

    private Command CreateDiffCommand()
    {
        var leftRevisionArgument = new Argument<string?>("left")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The optional revision on the left side of the comparison.",
        };
        var rightRevisionArgument = new Argument<string?>("right")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The optional revision on the right side of the comparison.",
        };
        var pathspecArgument = new Argument<string[]>("pathspec")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Repository paths following the option terminator.",
        };
        var command = new Command("diff", "Compare worktree, index, or revisions.")
        {
            leftRevisionArgument,
            rightRevisionArgument,
            pathspecArgument,
        };
        var cachedOption = new Option<bool>("--cached") { Description = "Compare staged changes." };
        command.Options.Add(cachedOption);
        var pathspecOptions = AddPathspecOptions(command);
        command.SetAction((parseResult, _) => RunShellAsync(
            ApplicationMode.Diff,
            workingDirectory: null,
            diff: BindDiffOptions(
                parseResult,
                cachedOption,
                leftRevisionArgument,
                rightRevisionArgument,
                pathspecArgument,
                pathspecOptions,
                _nativePathsAfterDoubleDash)));
        return command;
    }

    private Command CreateMergeCommand()
    {
        var pathArgument = new Argument<string[]>("paths")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Repository paths following the option terminator.",
        };
        var command = new Command("merge", "Resolve unmerged paths.")
        {
            pathArgument,
        };
        var pathspecOptions = AddPathspecOptions(command);
        command.SetAction((parseResult, _) => RunShellAsync(
            ApplicationMode.Merge,
            workingDirectory: null,
            merge: new MergeCommandOptions(
                parseResult.GetValue(pathArgument)?.ToImmutableArray() ?? [],
                parseResult.GetValue(pathspecOptions.FromFile),
                parseResult.GetValue(pathspecOptions.FileNul),
                _nativePathsAfterDoubleDash)));
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
            history: BindHistoryOptions(
                parseResult,
                revisionRangeArgument,
                pathspecArgument,
                pathspecOptions,
                _nativePathsAfterDoubleDash)));
        return command;
    }

    private Command CreateRebaseCommand()
    {
        var ontoOption = new Option<string?>("--onto")
        {
            Description = "Rebase onto the specified revision.",
            HelpName = "revision",
        };
        var upstreamArgument = new Argument<string?>("upstream")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The upstream revision.",
        };
        var command = new Command("rebase", "Plan or continue an interactive rebase.")
        {
            ontoOption,
            upstreamArgument,
        };
        command.SetAction((parseResult, _) => RunShellAsync(
            ApplicationMode.Rebase,
            workingDirectory: null,
            rebase: new RebaseOptions(
                parseResult.GetValue(upstreamArgument),
                parseResult.GetValue(ontoOption))));
        return command;
    }

    private Command CreateDoctorCommand()
    {
        var jsonOption = new Option<bool>("--json") { Description = "Write the stable machine-readable report." };
        var command = new Command("doctor", "Inspect installation and runtime capabilities.") { jsonOption };
        command.SetAction((parseResult, _) => WriteDoctorAsync(
            parseResult.GetValue(jsonOption),
            parseResult.InvocationConfiguration.Output));
        return command;
    }

    private async Task<int> WriteDoctorAsync(bool json, TextWriter output)
    {
        var report = await DoctorReportService.CreateAsync(
            new RuntimeProcessEnvironment(),
            CanonicalDirectory.Create(Environment.CurrentDirectory),
            _cancellationToken).ConfigureAwait(false);
        DoctorReportWriter.Write(json, report, output);
        return report.Git.Available ? ExitCodes.Success : ExitCodes.Failure;
    }

    private static Command CreateHelpCommand(RootCommand rootCommand)
    {
        var topicArgument = new Argument<string?>("command")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The command whose help should be displayed.",
        };
        topicArgument.CompletionSources.Add(_ => rootCommand.Subcommands
            .Where(static command => !command.Hidden)
            .Select(static command => new CompletionItem(command.Name))
            .ToArray());
        var command = new Command("help", "Show the embedded offline command manual.") { topicArgument };
        command.SetAction(parseResult =>
        {
            var topic = parseResult.GetValue(topicArgument);
            var helpArguments = topic is null ? s_rootHelpArguments : new[] { topic, "--help" };
            var exitCode = rootCommand.Parse(helpArguments).Invoke(parseResult.InvocationConfiguration);
            if (exitCode == ExitCodes.Success && topic is null)
            {
                OfflineManualRenderer.Write(parseResult.InvocationConfiguration.Output);
            }

            return exitCode;
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
            CompletionRenderer.Write(
                rootCommand,
                parseResult.GetValue(shellArgument)!,
                parseResult.InvocationConfiguration.Output);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateCompletionCandidatesCommand(RootCommand rootCommand)
    {
        var wordsArgument = new Argument<string[]>("words")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "The managed command words supplied by a generated shell completion script.",
        };
        var command = new Command(
            "completion-candidates",
            "Return private command-model candidates to a generated shell completion script.")
        {
            wordsArgument,
        };
        command.Hidden = true;
        command.SetAction(parseResult =>
        {
            var words = parseResult.GetValue(wordsArgument) ?? [];
            var hiddenNames = rootCommand.Subcommands
                .Where(static candidate => candidate.Hidden)
                .Select(static candidate => candidate.Name)
                .ToHashSet(StringComparer.Ordinal);
            var output = parseResult.InvocationConfiguration.Output;
            var input = string.Join(' ', words);
            var prefix = words.LastOrDefault() ?? string.Empty;
            foreach (var candidate in rootCommand.Parse(input)
                .GetCompletions(input.Length)
                .Select(static candidate => candidate.InsertText)
                .OfType<string>()
                .Where(candidate => candidate.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
                .Where(candidate => !hiddenNames.Contains(candidate))
                .Distinct(StringComparer.Ordinal))
            {
                output.WriteLine(candidate);
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateVersionCommand()
    {
        var command = new Command("version", "Print the GitSail version.");
        command.SetAction(parseResult => WriteVersionAsync(parseResult.InvocationConfiguration.Output));
        return command;
    }

    private Command CreateSequenceEditorCommand()
    {
        var todoPathArgument = new Argument<string>("todo-file")
        {
            Arity = ArgumentArity.ExactlyOne,
            Description = "The exact interactive-rebase todo path supplied by Git.",
        };
        var command = new Command(
            "sequence-editor",
            "Edit an authenticated Git interactive-rebase todo file.")
        {
            todoPathArgument,
        };
        command.Hidden = true;
        command.SetAction((parseResult, _) =>
        {
            var managedPath = parseResult.GetValue(todoPathArgument)!;
            var paths = _nativePathsAfterDoubleDash ?? CommandPathspecResolver.Convert([managedPath]);
            return paths.Length == 1
                ? RunSequenceEditorAsync(paths[0])
                : Task.FromResult(ExitCodes.Usage);
        });
        return command;
    }

    private Task<int> RunSequenceEditorAsync(GitPath todoPath)
    {
        if (_sequenceEditorRunner is not null)
        {
            return _sequenceEditorRunner(todoPath, _cancellationToken);
        }

        return SequenceEditorShell.RunAsync(todoPath, _cancellationToken);
    }

    private Command CreateInteractiveCommand(string name, string description, ApplicationMode mode)
    {
        var command = new Command(name, description);
        command.SetAction((_, _) => RunShellAsync(mode));
        return command;
    }

    private Task<int> RunShellAsync(ApplicationMode mode)
        => RunShellAsync(mode, workingDirectory: null);

    private async Task<int> RunShellAsync(
        ApplicationMode mode,
        string? workingDirectory,
        CitoolOptions? citool = null,
        HistoryOptions? history = null,
        BrowserOptions? browser = null,
        BlameOptions? blame = null,
        DiffOptions? diff = null,
        RebaseOptions? rebase = null,
        MergeCommandOptions? merge = null,
        TraceOptions? trace = null)
    {
        var options = new GitSailShellOptions(
            mode,
            workingDirectory,
            citool,
            history,
            browser,
            blame,
            diff,
            rebase,
            merge,
            trace);
        if (trace is null)
        {
            return await RunSelectedShellAsync(options).ConfigureAwait(false);
        }

        TraceSession session;
        try
        {
            session = TraceSession.Create(
                trace,
                new RuntimeProcessEnvironment(),
                TimeProvider.System);
        }
        catch (Exception exception) when (exception is ArgumentException or
            IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await Console.Error.WriteLineAsync(
                $"GitSail could not start trace capture: {TerminalTextSanitizer.Sanitize(exception.Message)}")
                .ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var generatedPath = session.GeneratedPath;
        var tracePath = session.FilePath;
        try
        {
            using (session)
            using (ApplicationTrace.Begin(session))
            {
                session.WriteApplicationStarted(mode);
                try
                {
                    var exitCode = await RunSelectedShellAsync(options).ConfigureAwait(false);
                    session.WriteApplicationCompleted(exitCode);
                    return exitCode;
                }
                catch (Exception exception)
                {
                    session.WriteApplicationFailed(exception);
                    throw;
                }
            }
        }
        finally
        {
            if (generatedPath)
            {
                await Console.Out.WriteLineAsync(tracePath).ConfigureAwait(false);
            }
        }
    }

    private Task<int> RunSelectedShellAsync(GitSailShellOptions options)
    {
        if (_shellRunner is not null)
        {
            return _shellRunner(options, _cancellationToken);
        }

        var shell = new GitSailShell(options);
        return RunShellCoreAsync(shell);
    }

    private async Task<int> RunShellCoreAsync(GitSailShell shell)
        => await shell.RunAsync(_cancellationToken).ConfigureAwait(false);

    private static async Task<int> WriteVersionAsync(TextWriter output)
    {
        await output.WriteLineAsync(BuildInformation.DisplayVersion).ConfigureAwait(false);
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

    private static TraceOptions? BindTraceOptions(
        ParseResult parseResult,
        Option<string?> traceOption)
        => parseResult.GetResult(traceOption) is null
            ? null
            : new TraceOptions(parseResult.GetValue(traceOption));

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

    private static BrowserOptions BindBrowserOptions(
        ParseResult parseResult,
        Argument<string?> revisionArgument,
        Argument<string?> directoryArgument,
        (Option<string?> FromFile, Option<bool> FileNul) pathspecOptions,
        ImmutableArray<GitPath>? nativePathsAfterDoubleDash)
    {
        var revision = parseResult.GetValue(revisionArgument);
        var directory = parseResult.GetValue(directoryArgument);
        if (IsAfterDoubleDash(parseResult, revisionArgument))
        {
            directory = revision;
            revision = null;
        }

        return new BrowserOptions(
            revision,
            directory is null ? [] : [directory],
            parseResult.GetValue(pathspecOptions.FromFile),
            parseResult.GetValue(pathspecOptions.FileNul),
            nativePathsAfterDoubleDash);
    }

    private static BlameOptions BindBlameOptions(
        ParseResult parseResult,
        Argument<string?> revisionArgument,
        Argument<string?> pathArgument,
        Option<int?> lineOption,
        Option<string?> rangeOption,
        Option<bool> detectMovesOption,
        Option<bool> detectCopiesOption,
        (Option<string?> FromFile, Option<bool> FileNul) pathspecOptions,
        ImmutableArray<GitPath>? nativePathsAfterDoubleDash)
    {
        var revision = parseResult.GetValue(revisionArgument);
        var path = parseResult.GetValue(pathArgument);
        if (revision is not null && IsAfterDoubleDash(parseResult, revisionArgument))
        {
            path = revision;
            revision = null;
        }
        else if (revision is not null && path is null && parseResult.GetValue(pathspecOptions.FromFile) is null)
        {
            path = revision;
            revision = null;
        }

        return new BlameOptions(
            revision,
            path is null ? [] : [path],
            parseResult.GetValue(lineOption),
            parseResult.GetValue(rangeOption),
            parseResult.GetValue(detectMovesOption),
            parseResult.GetValue(detectCopiesOption),
            parseResult.GetValue(pathspecOptions.FromFile),
            parseResult.GetValue(pathspecOptions.FileNul),
            nativePathsAfterDoubleDash);
    }

    private static HistoryOptions BindHistoryOptions(
        ParseResult parseResult,
        Argument<string?> revisionArgument,
        Argument<string[]> pathspecArgument,
        (Option<string?> FromFile, Option<bool> FileNul) pathspecOptions,
        ImmutableArray<GitPath>? nativePathsAfterDoubleDash)
    {
        var revision = parseResult.GetValue(revisionArgument);
        var pathspecs = parseResult.GetValue(pathspecArgument)?.ToImmutableArray() ?? [];
        if (revision is not null && IsAfterDoubleDash(parseResult, revisionArgument))
        {
            pathspecs = pathspecs.Insert(0, revision);
            revision = null;
        }

        return new HistoryOptions(
            revision,
            pathspecs,
            parseResult.GetValue(pathspecOptions.FromFile),
            parseResult.GetValue(pathspecOptions.FileNul),
            nativePathsAfterDoubleDash);
    }

    private static DiffOptions BindDiffOptions(
        ParseResult parseResult,
        Option<bool> cachedOption,
        Argument<string?> leftRevisionArgument,
        Argument<string?> rightRevisionArgument,
        Argument<string[]> pathspecArgument,
        (Option<string?> FromFile, Option<bool> FileNul) pathspecOptions,
        ImmutableArray<GitPath>? nativePathsAfterDoubleDash)
    {
        var leftRevision = parseResult.GetValue(leftRevisionArgument);
        var rightRevision = parseResult.GetValue(rightRevisionArgument);
        var pathspecs = ImmutableArray.CreateBuilder<string>();
        if (leftRevision is not null && IsAfterDoubleDash(parseResult, leftRevisionArgument))
        {
            pathspecs.Add(leftRevision);
            leftRevision = null;
        }

        if (rightRevision is not null && IsAfterDoubleDash(parseResult, rightRevisionArgument))
        {
            pathspecs.Add(rightRevision);
            rightRevision = null;
        }

        pathspecs.AddRange(parseResult.GetValue(pathspecArgument) ?? []);
        return new DiffOptions(
            parseResult.GetValue(cachedOption),
            leftRevision,
            rightRevision,
            pathspecs.ToImmutable(),
            parseResult.GetValue(pathspecOptions.FromFile),
            parseResult.GetValue(pathspecOptions.FileNul),
            nativePathsAfterDoubleDash);
    }

    private static bool IsAfterDoubleDash(ParseResult parseResult, Argument argument)
    {
        var argumentTokens = parseResult.GetResult(argument)?.Tokens;
        if (argumentTokens is null || argumentTokens.Count == 0)
        {
            return false;
        }

        var sawDoubleDash = false;
        foreach (var token in parseResult.Tokens)
        {
            if (token.Type == TokenType.DoubleDash)
            {
                sawDoubleDash = true;
            }
            else if (argumentTokens.Any(argumentToken => ReferenceEquals(argumentToken, token)))
            {
                return sawDoubleDash;
            }
        }

        return false;
    }

}
