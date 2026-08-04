using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Resolves revisions and lazily reads exact immutable Git trees and blobs.
/// </summary>
internal sealed class TreeService
{
    private const int MaximumTreeBytes = 512 * 1024 * 1024;
    private const int MaximumBlobBytes = 1024 * 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private const int BlobMemoryThresholdBytes = 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RevisionResolver _revisionResolver;
    private readonly TreeEntryParser _parser;

    /// <summary>
    /// Initializes immutable tree browsing over the typed child-process boundary.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal TreeService(
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
        _parser = new TreeEntryParser();
    }

    /// <summary>
    /// Resolves one literal revision and optional exact directory to its first immutable listing.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="revision">The literal revision candidate.</param>
    /// <param name="directory">The optional exact repository-relative directory.</param>
    /// <param name="cancellationToken">Signals revision and tree capture cancellation.</param>
    /// <returns>The exact commit, tree, directory, and immediate entries.</returns>
    internal async Task<TreeCatalog> OpenAsync(
        CanonicalDirectory workingDirectory,
        Revision revision,
        GitPath? directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(revision);
        var resolved = await _revisionResolver.ResolveCommitAsync(
            workingDirectory,
            revision,
            cancellationToken).ConfigureAwait(false);
        var rootTree = await ResolveRootTreeAsync(
            workingDirectory,
            resolved.CommitObjectId,
            cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return await ListCoreAsync(
                workingDirectory,
                resolved.CommitObjectId,
                rootTree,
                directory: null,
                cancellationToken).ConfigureAwait(false);
        }

        var matches = await CaptureEntriesAsync(
            workingDirectory,
            resolved.CommitObjectId,
            directory,
            cancellationToken).ConfigureAwait(false);
        var match = matches.FirstOrDefault(entry => entry.Name.Equals(directory));
        if (match is null || match.Kind != TreeEntryKind.Tree)
        {
            throw new GitCommandException(1, "The selected revision does not contain that directory.");
        }

        return await ListCoreAsync(
            workingDirectory,
            resolved.CommitObjectId,
            match.ObjectId,
            directory,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lazily lists one exact selected nested tree object.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="commitObjectId">The exact owning commit object identifier.</param>
    /// <param name="treeObjectId">The exact selected tree object identifier.</param>
    /// <param name="directory">The exact repository-relative directory.</param>
    /// <param name="cancellationToken">Signals tree capture cancellation.</param>
    /// <returns>The exact immediate tree entries.</returns>
    internal async Task<TreeCatalog> ListAsync(
        CanonicalDirectory workingDirectory,
        ObjectId commitObjectId,
        ObjectId treeObjectId,
        GitPath directory,
        CancellationToken cancellationToken)
        => await ListCoreAsync(
            workingDirectory,
            commitObjectId,
            treeObjectId,
            (GitPath?)directory,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Captures one exact blob or symbolic-link payload into bounded spillable storage.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="entry">The exact blob-backed tree entry.</param>
    /// <param name="cancellationToken">Signals blob capture cancellation.</param>
    /// <returns>The owned exact byte spool that the caller must dispose.</returns>
    internal async Task<RawByteSpool> ReadBlobAsync(
        CanonicalDirectory workingDirectory,
        TreeEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind is TreeEntryKind.Tree or TreeEntryKind.GitLink)
        {
            throw new ArgumentException("Only blob-backed tree entries can be read as content.", nameof(entry));
        }

        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("cat-file"),
                ProcessArgument.Literal("blob"),
                ProcessArgument.Native(entry.ObjectId),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.CreateSpooling(
                BlobMemoryThresholdBytes,
                MaximumBlobBytes,
                MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        var spool = result.StandardOutputSpool
            ?? throw new InvalidOperationException("Blob capture did not return its required byte spool.");
        if (result.ExitCode != 0)
        {
            spool.Dispose();
            throw CreateCommandException(result, "Git could not read the selected tree object.");
        }

        return spool;
    }

    private async Task<TreeCatalog> ListCoreAsync(
        CanonicalDirectory workingDirectory,
        ObjectId commitObjectId,
        ObjectId treeObjectId,
        GitPath? directory,
        CancellationToken cancellationToken)
    {
        var entries = await CaptureEntriesAsync(
            workingDirectory,
            treeObjectId,
            path: null,
            cancellationToken).ConfigureAwait(false);
        return new TreeCatalog(commitObjectId, treeObjectId, directory, entries);
    }

    private async Task<ImmutableArray<TreeEntry>> CaptureEntriesAsync(
        CanonicalDirectory workingDirectory,
        ObjectId treeish,
        GitPath? path,
        CancellationToken cancellationToken)
    {
        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("--literal-pathspecs"),
            ProcessArgument.Literal("--no-pager"),
            ProcessArgument.Literal("ls-tree"),
            ProcessArgument.Literal("--long"),
            ProcessArgument.Literal("-z"),
            ProcessArgument.Literal("--full-name"),
            ProcessArgument.Native(treeish),
        };
        if (path is not null)
        {
            arguments.Add(ProcessArgument.Literal("--"));
            arguments.Add(ProcessArgument.Native(path));
        }

        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. arguments],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumTreeBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not list the selected tree.");
        }

        return _parser.Parse(result.StandardOutput.Span);
    }

    private async Task<ObjectId> ResolveRootTreeAsync(
        CanonicalDirectory workingDirectory,
        ObjectId commit,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("rev-parse"),
                ProcessArgument.Literal("--verify"),
                ProcessArgument.Literal("--end-of-options"),
                ProcessArgument.Literal(commit + "^{tree}"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(4096, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(result, "Git could not resolve the selected commit tree.");
        }

        var output = result.StandardOutput.Span;
        if (!output.IsEmpty && output[^1] == (byte)'\n')
        {
            output = output[..^1];
            if (OperatingSystem.IsWindows() && !output.IsEmpty && output[^1] == (byte)'\r')
            {
                output = output[..^1];
            }
        }

        if (!ObjectId.TryParseHex(output, out var tree))
        {
            throw new InvalidDataException("Git returned an invalid root tree object identifier.");
        }

        return tree!;
    }

    private static GitCommandException CreateCommandException(ProcessResult result, string fallback)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        return new GitCommandException(result.ExitCode, string.IsNullOrEmpty(error) ? fallback : error);
    }
}
