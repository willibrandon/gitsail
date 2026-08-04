using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Captures exact file bytes and structured incremental line attribution through Git.
/// </summary>
internal sealed class BlameService
{
    private const int MaximumContentBytes = 1024 * 1024 * 1024;
    private const int MaximumBlameBytes = 512 * 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RevisionResolver _revisionResolver;
    private readonly BlameIncrementalParser _parser;

    /// <summary>
    /// Initializes structured blame capture over the typed child-process boundary.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal BlameService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _revisionResolver = new RevisionResolver(installation, runner, environmentFactory);
        _parser = new BlameIncrementalParser();
    }

    /// <summary>
    /// Captures one exact file version and its bounded per-line incremental attribution.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="repository">The discovered repository containing the requested path.</param>
    /// <param name="request">The typed revision, path, range, and detection request.</param>
    /// <param name="cancellationToken">Signals content and blame capture cancellation.</param>
    /// <returns>The exact content and separate ordered attribution catalog.</returns>
    internal async Task<BlameCatalog> CaptureAsync(
        CanonicalDirectory workingDirectory,
        RepositoryLocation repository,
        BlameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(request);

        ObjectId? resolvedRevision = null;
        byte[] content;
        if (request.Revision is null)
        {
            var absolutePath = RepositoryWorkTreePathService.Resolve(repository, request.Path);
            content = await RepositoryStateFileSystem.ReadIfExistsAsync(
                absolutePath,
                MaximumContentBytes,
                cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The selected worktree file does not exist.");
        }
        else
        {
            var resolved = await _revisionResolver.ResolveCommitAsync(
                workingDirectory,
                request.Revision,
                cancellationToken).ConfigureAwait(false);
            resolvedRevision = resolved.CommitObjectId;
            content = await ReadRevisionContentAsync(
                workingDirectory,
                resolvedRevision,
                request.Path,
                cancellationToken).ConfigureAwait(false);
        }

        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("--literal-pathspecs"),
            ProcessArgument.Literal("--no-pager"),
            ProcessArgument.Literal("blame"),
            ProcessArgument.Literal("--incremental"),
            ProcessArgument.Literal("--encoding=UTF-8"),
        };
        if (request.DetectMoves)
        {
            arguments.Add(ProcessArgument.Literal("-M"));
        }

        if (request.DetectCopies)
        {
            arguments.Add(ProcessArgument.Literal("-C"));
        }

        if (request.Range is not null)
        {
            arguments.Add(ProcessArgument.Literal("-L"));
            arguments.Add(ProcessArgument.Literal(request.Range.ToString()));
        }

        StandardInputSource standardInput;
        if (resolvedRevision is null)
        {
            arguments.Add(ProcessArgument.Literal("--contents"));
            arguments.Add(ProcessArgument.Literal("-"));
            standardInput = StandardInputSource.FromBytes(content);
        }
        else
        {
            arguments.Add(ProcessArgument.Native(resolvedRevision));
            standardInput = StandardInputSource.Empty();
        }

        arguments.Add(ProcessArgument.Literal("--"));
        arguments.Add(ProcessArgument.Native(request.Path));
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. arguments],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            standardInput,
            OutputPolicy.Create(MaximumBlameBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not load line history for the selected file.");
        }

        var attributions = _parser.Parse(result.StandardOutput.Span);
        ValidateAttributions(content, attributions);
        var encodingName = await ReadEncodingNameAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        return new BlameCatalog(request.Path, resolvedRevision, content, attributions, encodingName);
    }

    private async Task<byte[]> ReadRevisionContentAsync(
        CanonicalDirectory workingDirectory,
        ObjectId commit,
        GitPath path,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("cat-file"),
                ProcessArgument.Literal("blob"),
                CreateObjectExpression(commit, path),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumContentBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "The selected revision does not contain that file.");
        }

        return result.StandardOutput.ToArray();
    }

    private async Task<string> ReadEncodingNameAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("config"),
                ProcessArgument.Literal("--get"),
                ProcessArgument.Literal("gui.encoding"),
            ],
            workingDirectory,
            _environmentFactory.CreateConfigurationReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(4096, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1)
        {
            return "UTF-8";
        }

        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not read gui.encoding.");
        }

        var value = Encoding.UTF8.GetString(result.StandardOutput.Span).Trim();
        return string.IsNullOrWhiteSpace(value) ? "UTF-8" : value;
    }

    private static ProcessArgument CreateObjectExpression(ObjectId commit, GitPath path)
    {
        var prefix = Encoding.ASCII.GetBytes(commit + ":");
        if (OperatingSystem.IsWindows())
        {
            return ProcessArgument.Literal(
                Encoding.ASCII.GetString(prefix) + path.GetWindowsPath().Replace('\\', '/'));
        }

        var pathBytes = path.GetUnixBytes();
        var expression = new byte[checked(prefix.Length + pathBytes.Length)];
        prefix.CopyTo(expression, 0);
        pathBytes.CopyTo(expression.AsSpan(prefix.Length));
        return ProcessArgument.FromUnixBytes(expression);
    }

    private static void ValidateAttributions(
        ReadOnlySpan<byte> content,
        System.Collections.Immutable.ImmutableArray<BlameAttribution> attributions)
    {
        var lineCount = content.IsEmpty ? 0 : content.Count((byte)'\n') + (content[^1] == (byte)'\n' ? 0 : 1);
        var previousLine = 0;
        foreach (var attribution in attributions)
        {
            if (attribution.ResultLineNumber <= previousLine || attribution.ResultLineNumber > lineCount)
            {
                throw new InvalidDataException("Git blame returned duplicate or out-of-range result lines.");
            }

            previousLine = attribution.ResultLineNumber;
        }
    }

    private static GitCommandException CreateCommandException(ProcessResult result, string fallback)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        return new GitCommandException(result.ExitCode, string.IsNullOrEmpty(error) ? fallback : error);
    }
}
