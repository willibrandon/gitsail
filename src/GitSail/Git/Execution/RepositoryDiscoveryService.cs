using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Discovers canonical repository locations exclusively through read-only Git commands.
/// </summary>
internal sealed class RepositoryDiscoveryService
{
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;

    /// <summary>
    /// Initializes repository discovery for one resolved Git installation.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    internal RepositoryDiscoveryService(GitInstallation installation, IChildProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        _installation = installation;
        _runner = runner;
    }

    /// <summary>
    /// Discovers the repository containing a canonical working directory.
    /// </summary>
    /// <param name="workingDirectory">The directory from which Git discovery starts.</param>
    /// <param name="cancellationToken">Signals discovery cancellation.</param>
    /// <returns>The canonical repository locations and storage format.</returns>
    internal async Task<RepositoryLocation> DiscoverAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var gitDirectory = await QueryPathAsync(
            workingDirectory,
            ["--absolute-git-dir"],
            allowEmpty: false,
            cancellationToken).ConfigureAwait(false);
        var commonDirectory = await QueryPathAsync(
            workingDirectory,
            ["--path-format=absolute", "--git-common-dir"],
            allowEmpty: false,
            cancellationToken).ConfigureAwait(false);
        var isBareBytes = await QueryValueAsync(
            workingDirectory,
            ["--is-bare-repository"],
            cancellationToken).ConfigureAwait(false);
        bool isBare;
        if (isBareBytes.Span.SequenceEqual("true"u8))
        {
            isBare = true;
        }
        else if (isBareBytes.Span.SequenceEqual("false"u8))
        {
            isBare = false;
        }
        else
        {
            throw new InvalidDataException("Git returned an invalid bare-repository response.");
        }
        var objectFormatBytes = await QueryValueAsync(
            workingDirectory,
            ["--show-object-format=storage"],
            cancellationToken).ConfigureAwait(false);
        var objectFormat = objectFormatBytes.Span.SequenceEqual("sha1"u8)
            ? RepositoryObjectFormat.Sha1
            : objectFormatBytes.Span.SequenceEqual("sha256"u8)
                ? RepositoryObjectFormat.Sha256
                : throw new InvalidDataException("Git returned an unsupported object format.");
        var prefix = await QueryPathAsync(
            workingDirectory,
            ["--show-prefix"],
            allowEmpty: true,
            cancellationToken).ConfigureAwait(false);

        GitPath? workTree = null;
        if (!isBare)
        {
            workTree = await QueryPathAsync(
                workingDirectory,
                ["--path-format=absolute", "--show-toplevel"],
                allowEmpty: false,
                cancellationToken).ConfigureAwait(false);
        }

        return new RepositoryLocation(
            gitDirectory!,
            commonDirectory!,
            workTree,
            prefix,
            objectFormat,
            isBare);
    }

    private async Task<GitPath?> QueryPathAsync(
        CanonicalDirectory workingDirectory,
        string[] arguments,
        bool allowEmpty,
        CancellationToken cancellationToken)
    {
        var bytes = await QueryValueAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false);
        if (bytes.IsEmpty)
        {
            return allowEmpty ? null : throw new InvalidDataException("Git returned an empty repository path.");
        }

        return OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(Encoding.UTF8.GetString(bytes.Span))
            : GitPath.FromUnixBytes(bytes.Span);
    }

    private async Task<ReadOnlyMemory<byte>> QueryValueAsync(
        CanonicalDirectory workingDirectory,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var invocationArguments = new List<ProcessArgument>(arguments.Length + 2)
        {
            ProcessArgument.Literal("--no-pager"),
            ProcessArgument.Literal("rev-parse"),
        };
        invocationArguments.AddRange(arguments.Select(ProcessArgument.Literal));
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. invocationArguments],
            workingDirectory,
            CreateReadOnlyEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, 64 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git repository discovery failed." : error);
        }

        var output = result.StandardOutput;
        if (!output.IsEmpty && output.Span[^1] == (byte)'\n')
        {
            output = output[..^1];
            if (OperatingSystem.IsWindows() && !output.IsEmpty && output.Span[^1] == (byte)'\r')
            {
                output = output[..^1];
            }
        }

        return output;
    }

    private static ChildEnvironment CreateReadOnlyEnvironment()
        => ChildEnvironment.Create(
        [
            new KeyValuePair<string, string>("LANG", "C"),
            new KeyValuePair<string, string>("LC_ALL", "C"),
            new KeyValuePair<string, string>("GIT_PAGER", "cat"),
            new KeyValuePair<string, string>("GIT_OPTIONAL_LOCKS", "0"),
        ]);
}
