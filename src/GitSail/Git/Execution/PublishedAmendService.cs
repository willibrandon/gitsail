using GitSail.Domain;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Finds local remote-tracking refs that make amending a commit potentially published history rewriting.
/// </summary>
internal sealed class PublishedAmendService
{
    private const int MaximumOutputBytes = 16 * 1024 * 1024;
    private const int MaximumErrorBytes = 1024 * 1024;
    private const int MaximumRecordBytes = 1024 * 1024;
    private static ReadOnlySpan<byte> RemoteTrackingPrefix => "refs/remotes/"u8;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;

    /// <summary>
    /// Initializes local publication detection over structured Git reference output.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal PublishedAmendService(
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
    }

    /// <summary>
    /// Finds every nonsymbolic local remote-tracking ref containing one exact commit.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="commit">The exact commit that would be amended.</param>
    /// <param name="cancellationToken">Signals reference-query cancellation.</param>
    /// <returns>A complete local-heuristic warning, or <see langword="null"/> when no ref contains the commit.</returns>
    internal async Task<PublishedAmendWarning?> FindAsync(
        CanonicalDirectory workingDirectory,
        ObjectId? commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        if (commit is null)
        {
            return null;
        }

        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("for-each-ref"),
                ProcessArgument.Literal($"--contains={commit}"),
                ProcessArgument.Literal("--format=%(refname)%09%(symref)"),
                ProcessArgument.Literal("refs/remotes/"),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumOutputBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error)
                    ? "Git could not inspect remote-tracking refs for amend safety."
                    : error);
        }

        var refs = Parse(result.StandardOutput.Span);
        return refs.IsEmpty ? null : new PublishedAmendWarning(refs);
    }

    private static ImmutableArray<RefName> Parse(ReadOnlySpan<byte> output)
    {
        var refs = ImmutableArray.CreateBuilder<RefName>();
        while (!output.IsEmpty)
        {
            var lineEnding = output.IndexOf((byte)'\n');
            if (lineEnding < 0)
            {
                throw new InvalidDataException("Git remote-tracking reference output ended before a line terminator.");
            }

            if (lineEnding > MaximumRecordBytes)
            {
                throw new InvalidDataException("Git returned a remote-tracking reference record above the limit.");
            }

            var record = output[..lineEnding];
            output = output[(lineEnding + 1)..];
            var separator = record.IndexOf((byte)'\t');
            if (separator <= 0)
            {
                throw new InvalidDataException("Git returned malformed remote-tracking reference output.");
            }

            var refName = record[..separator];
            var symbolicTarget = record[(separator + 1)..];
            if (!refName.StartsWith(RemoteTrackingPrefix))
            {
                throw new InvalidDataException("Git returned a reference outside the remote-tracking namespace.");
            }

            if (symbolicTarget.IsEmpty)
            {
                refs.Add(RefName.FromBytes(refName));
            }
        }

        return refs.ToImmutable();
    }
}
