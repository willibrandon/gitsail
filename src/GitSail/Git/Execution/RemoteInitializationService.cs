using GitSail.Domain;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Plans and executes isolated local or fixed-script SSH bare-repository initialization.
/// </summary>
internal sealed class RemoteInitializationService
{
    private const int MaximumOutputBytes = 64 * 1024 * 1024;
    private const int MaximumErrorBytes = 16 * 1024 * 1024;
    private const string ProbeMarkerPrefix = "GITSAIL_INIT_CAP_V1:";
    private const string SuccessMarker = "GITSAIL_INIT_OK_V1\n";
    private const string ProbeScript = """
set -eu
if ! command -v git >/dev/null 2>&1; then
    printf 'GITSAIL_INIT_NO_GIT\n'
    exit 69
fi
if command -v base64 >/dev/null 2>&1 && [ "$(printf 'Rw==' | base64 --decode 2>/dev/null)" = G ]; then
    printf 'GITSAIL_INIT_CAP_V1:gnu\n'
elif command -v base64 >/dev/null 2>&1 && [ "$(printf 'Rw==' | base64 -D 2>/dev/null)" = G ]; then
    printf 'GITSAIL_INIT_CAP_V1:bsd\n'
elif command -v openssl >/dev/null 2>&1 && [ "$(printf 'Rw==' | openssl base64 -d -A 2>/dev/null)" = G ]; then
    printf 'GITSAIL_INIT_CAP_V1:openssl\n'
else
    printf 'GITSAIL_INIT_NO_DECODER\n'
    exit 69
fi
""";
    private const string InitializationScriptPrefix = """
set -eu
{
IFS= read -r protocol
IFS= read -r object_format
IFS= read -r payload
} <<'GITSAIL.FRAME'
""";
    private const string InitializationScriptSuffix = """
[ "$protocol" = GITSAIL_INIT_FRAME_V1 ] || exit 64
case "$object_format" in sha1|sha256) ;; *) exit 64 ;; esac
case "$payload" in ''|*[!A-Za-z0-9_-]*) exit 64 ;; esac
case $((${#payload} % 4)) in
    0) padding='' ;;
    2) padding='==' ;;
    3) padding='=' ;;
    *) exit 64 ;;
esac
encoded=$(printf '%s%s' "$payload" "$padding" | tr '_-' '/+')
if command -v base64 >/dev/null 2>&1 && [ "$(printf 'Rw==' | base64 --decode 2>/dev/null)" = G ]; then
    decoded_with_marker=$(printf '%s' "$encoded" | base64 --decode; printf x)
elif command -v base64 >/dev/null 2>&1 && [ "$(printf 'Rw==' | base64 -D 2>/dev/null)" = G ]; then
    decoded_with_marker=$(printf '%s' "$encoded" | base64 -D; printf x)
elif command -v openssl >/dev/null 2>&1 && [ "$(printf 'Rw==' | openssl base64 -d -A 2>/dev/null)" = G ]; then
    decoded_with_marker=$(printf '%s' "$encoded" | openssl base64 -d -A; printf x)
else
    exit 69
fi
path=${decoded_with_marker%x}
[ -n "$path" ] || exit 64
case "$path" in
    '~/'*) [ -n "${HOME:-}" ] || exit 64; path=$HOME/${path#'~/'} ;;
    '~'*) printf 'Remote initialization does not expand another user home.\n' >&2; exit 64 ;;
esac
case "$path" in /*) ;; *) path=./$path ;; esac
umask 077
if ! mkdir -m 700 "$path"; then
    printf 'Remote initialization could not create the target exclusively.\n' >&2
    exit 73
fi
git "--git-dir=$path" init --bare "--object-format=$object_format"
[ "$(git --git-dir="$path" rev-parse --is-bare-repository)" = true ]
[ "$(git --git-dir="$path" rev-parse --show-object-format=storage)" = "$object_format" ]
printf 'GITSAIL_INIT_OK_V1\n'
""";
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _coordinator;
    private readonly RemoteService _remoteService;
    private readonly ExecutableResolver _resolver;
    private readonly RepositoryObjectFormat _objectFormat;
    private readonly CredentialPromptBroker _credentialPromptBroker;

    /// <summary>
    /// Initializes remote repository creation over explicit Git, SSH, environment, and mutation boundaries.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="coordinator">The shared repository mutation coordinator.</param>
    /// <param name="remoteService">The stable configured-remote service.</param>
    /// <param name="resolver">The trusted executable resolver.</param>
    /// <param name="objectFormat">The current repository storage object format.</param>
    /// <param name="credentialPromptBroker">The operation-scoped authenticated credential broker.</param>
    internal RemoteInitializationService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        RepositoryMutationCoordinator coordinator,
        RemoteService remoteService,
        ExecutableResolver resolver,
        RepositoryObjectFormat objectFormat,
        CredentialPromptBroker credentialPromptBroker)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(remoteService);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(credentialPromptBroker);
        if (!Enum.IsDefined(objectFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(objectFormat));
        }

        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _coordinator = coordinator;
        _remoteService = remoteService;
        _resolver = resolver;
        _objectFormat = objectFormat;
        _credentialPromptBroker = credentialPromptBroker;
    }

    /// <summary>
    /// Resolves one configured push URL into an exact safe local or SSH confirmation plan.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="expectedCatalog">The exact complete remote catalog displayed to the user.</param>
    /// <param name="remote">The exact selected configured remote.</param>
    /// <param name="configuredUrlIndex">The selected configured push-URL index.</param>
    /// <param name="cancellationToken">Signals initialization planning cancellation.</param>
    /// <returns>The exact typed target and verified capability plan.</returns>
    internal async Task<RemoteInitializationPlan> PrepareAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteInfo remote,
        int configuredUrlIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        ArgumentNullException.ThrowIfNull(remote);
        if (configuredUrlIndex < 0 || configuredUrlIndex >= remote.PushUrls.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(configuredUrlIndex));
        }

        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.RemoteMutation,
            cancellationToken).ConfigureAwait(false);
        var liveRemote = await RevalidateRemoteAsync(
            workingDirectory,
            expectedCatalog,
            remote,
            cancellationToken).ConfigureAwait(false);
        var effectiveUrls = await CaptureEffectivePushUrlsAsync(
            workingDirectory,
            liveRemote,
            cancellationToken).ConfigureAwait(false);
        var target = RemoteInitializationTargetParser.Parse(effectiveUrls[configuredUrlIndex]);
        if (target.Kind == RemoteInitializationKind.Local)
        {
            target = CanonicalizeAvailableLocalTarget(target);
            return new RemoteInitializationPlan(
                expectedCatalog,
                liveRemote,
                configuredUrlIndex,
                target,
                _objectFormat,
                sshExecutable: null,
                sshDecoder: null);
        }

        var ssh = _resolver.Resolve(ProgramKind.Ssh);
        var decoder = await ProbeSshAsync(
            workingDirectory,
            target,
            ssh,
            cancellationToken).ConfigureAwait(false);
        return new RemoteInitializationPlan(
            expectedCatalog,
            liveRemote,
            configuredUrlIndex,
            target,
            _objectFormat,
            ssh,
            decoder);
    }

    /// <summary>
    /// Executes one exact confirmed initialization after revalidating configuration, target, and capability.
    /// </summary>
    /// <param name="workingDirectory">The canonical current repository working directory.</param>
    /// <param name="plan">The exact local or SSH initialization plan displayed to the user.</param>
    /// <param name="cancellationToken">Signals initialization cancellation.</param>
    /// <returns>The successful bounded Git or remote-script output.</returns>
    internal async Task<GitOperationResult> InitializeAsync(
        CanonicalDirectory workingDirectory,
        RemoteInitializationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(plan);
        await using var lease = await _coordinator.AcquireAsync(
            RepositoryMutationPurpose.RemoteMutation,
            cancellationToken).ConfigureAwait(false);
        var liveRemote = await RevalidateRemoteAsync(
            workingDirectory,
            plan.Catalog,
            plan.Remote,
            cancellationToken).ConfigureAwait(false);
        var effectiveUrls = await CaptureEffectivePushUrlsAsync(
            workingDirectory,
            liveRemote,
            cancellationToken).ConfigureAwait(false);
        var target = RemoteInitializationTargetParser.Parse(effectiveUrls[plan.ConfiguredUrlIndex]);
        if (target.Kind == RemoteInitializationKind.Local)
        {
            target = CanonicalizeAvailableLocalTarget(target);
        }

        if (!TargetsMatch(plan.Target, target))
        {
            throw new RepositoryPreconditionException(
                "The effective initialization target changed after confirmation; review a new plan.");
        }

        return plan.Target.Kind == RemoteInitializationKind.Local
            ? await InitializeLocalAsync(workingDirectory, plan, cancellationToken).ConfigureAwait(false)
            : await InitializeSshAsync(workingDirectory, plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitOperationResult> InitializeLocalAsync(
        CanonicalDirectory workingDirectory,
        RemoteInitializationPlan plan,
        CancellationToken cancellationToken)
    {
        var target = CanonicalizeAvailableLocalTarget(plan.Target);
        if (!string.Equals(target.LocalPath, plan.Target.LocalPath, PathComparison))
        {
            throw new RepositoryPreconditionException(
                "The canonical local initialization target changed after confirmation.");
        }

        CreateLocalDirectoryExclusive(target.LocalPath!);
        var gitDirectory = CreateNativePath(target.LocalPath!);
        var result = await RunGitAsync(
            workingDirectory,
            [
                CreatePrefixedPathArgument("--git-dir="u8, gitDirectory),
                ProcessArgument.Literal("init"),
                ProcessArgument.Literal("--bare"),
                ProcessArgument.Literal($"--object-format={FormatObjectFormat(plan.ObjectFormat)}"),
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(
                result,
                "Git could not initialize the exact local bare repository.",
                plan.Target.Url);
        }

        await VerifyLocalAsync(
            workingDirectory,
            gitDirectory,
            plan.ObjectFormat,
            cancellationToken).ConfigureAwait(false);
        return new GitOperationResult(result.StandardOutput, result.StandardError);
    }

    private async Task<GitOperationResult> InitializeSshAsync(
        CanonicalDirectory workingDirectory,
        RemoteInitializationPlan plan,
        CancellationToken cancellationToken)
    {
        var liveSsh = _resolver.Resolve(ProgramKind.Ssh);
        if (!Equals(liveSsh, plan.SshExecutable) || !ExecutableResolver.IsUnchanged(liveSsh))
        {
            throw new RepositoryPreconditionException(
                "The resolved SSH executable changed after confirmation; review a new plan.");
        }

        var decoder = await ProbeSshAsync(
            workingDirectory,
            plan.Target,
            liveSsh,
            cancellationToken).ConfigureAwait(false);
        if (decoder != plan.SshDecoder)
        {
            throw new RepositoryPreconditionException(
                "The remote SSH initialization capability changed after confirmation.");
        }

        var input = BuildInitializationInput(plan.Target.RemotePath!, plan.ObjectFormat);
        var result = await RunSshAsync(
            workingDirectory,
            plan.Target,
            liveSsh,
            input,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 ||
            !Encoding.UTF8.GetString(result.StandardOutput.Span).EndsWith(
                SuccessMarker,
                StringComparison.Ordinal))
        {
            throw CreateCommandException(
                result,
                "The fixed SSH program could not initialize and verify the exact remote bare repository.",
                plan.Target.Url);
        }

        return new GitOperationResult(result.StandardOutput, result.StandardError);
    }

    private async Task<SshBase64Decoder> ProbeSshAsync(
        CanonicalDirectory workingDirectory,
        RemoteInitializationTarget target,
        ResolvedExecutable ssh,
        CancellationToken cancellationToken)
    {
        var result = await RunSshAsync(
            workingDirectory,
            target,
            ssh,
            s_strictUtf8.GetBytes(ProbeScript),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(
                result,
                "The SSH server lacks the required POSIX shell, Git, or base64 decoder; initialize it manually.",
                target.Url);
        }

        var output = Encoding.ASCII.GetString(result.StandardOutput.Span).Trim();
        return output switch
        {
            $"{ProbeMarkerPrefix}gnu" => SshBase64Decoder.Gnu,
            $"{ProbeMarkerPrefix}bsd" => SshBase64Decoder.Bsd,
            $"{ProbeMarkerPrefix}openssl" => SshBase64Decoder.OpenSsl,
            _ => throw new RemoteInitializationException(
                "The SSH capability probe returned an unsupported response; initialize the remote manually."),
        };
    }

    private async Task VerifyLocalAsync(
        CanonicalDirectory workingDirectory,
        GitPath gitDirectory,
        RepositoryObjectFormat objectFormat,
        CancellationToken cancellationToken)
    {
        var bare = await RunGitAsync(
            workingDirectory,
            [
                CreatePrefixedPathArgument("--git-dir="u8, gitDirectory),
                ProcessArgument.Literal("rev-parse"),
                ProcessArgument.Literal("--is-bare-repository"),
            ],
            cancellationToken).ConfigureAwait(false);
        var format = await RunGitAsync(
            workingDirectory,
            [
                CreatePrefixedPathArgument("--git-dir="u8, gitDirectory),
                ProcessArgument.Literal("rev-parse"),
                ProcessArgument.Literal("--show-object-format=storage"),
            ],
            cancellationToken).ConfigureAwait(false);
        if (bare.ExitCode != 0 || !bare.StandardOutput.Span.SequenceEqual("true\n"u8) ||
            format.ExitCode != 0 || !Encoding.ASCII.GetString(format.StandardOutput.Span).Trim().Equals(
                FormatObjectFormat(objectFormat),
                StringComparison.Ordinal))
        {
            throw new RemoteInitializationException(
                "Git reported success, but the local target is not the requested bare repository format.");
        }
    }

    private async Task<RemoteInfo> RevalidateRemoteAsync(
        CanonicalDirectory workingDirectory,
        RemoteCatalog expectedCatalog,
        RemoteInfo remote,
        CancellationToken cancellationToken)
    {
        var liveCatalog = await _remoteService.CaptureAsync(
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!expectedCatalog.Matches(liveCatalog))
        {
            throw new RepositoryPreconditionException(
                "Remote names or URLs changed after the initialization view was prepared.");
        }

        var liveRemote = liveCatalog.Find(remote.Name);
        if (liveRemote is null || !liveRemote.Matches(remote))
        {
            throw new RepositoryPreconditionException(
                "The selected remote changed after it was displayed.");
        }

        return liveRemote;
    }

    private async Task<ImmutableArray<RemoteUrl>> CaptureEffectivePushUrlsAsync(
        CanonicalDirectory workingDirectory,
        RemoteInfo remote,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("remote"),
                ProcessArgument.Literal("get-url"),
                ProcessArgument.Literal("--push"),
                ProcessArgument.Literal("--all"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(remote.Name),
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(
                result,
                "Git could not resolve the selected remote's effective initialization URLs.",
                remote.PushUrls);
        }

        var urls = ParseEffectiveUrls(result.StandardOutput.Span);
        if (urls.Length != remote.PushUrls.Length)
        {
            throw new RemoteInitializationException(
                "Git returned an ambiguous effective push URL list; embedded line breaks are not accepted.");
        }

        return urls;
    }

    private Task<ProcessResult> RunGitAsync(
        CanonicalDirectory workingDirectory,
        ImmutableArray<ProcessArgument> arguments,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [ProcessArgument.Literal("--no-pager"), .. arguments],
            workingDirectory,
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumOutputBytes, MaximumErrorBytes));
        return _runner.RunAsync(invocation, cancellationToken);
    }

    private async Task<ProcessResult> RunSshAsync(
        CanonicalDirectory workingDirectory,
        RemoteInitializationTarget target,
        ResolvedExecutable ssh,
        byte[] standardInput,
        CancellationToken cancellationToken)
    {
        var arguments = ImmutableArray.CreateBuilder<ProcessArgument>();
        if (target.SshPort is { } port)
        {
            arguments.Add(ProcessArgument.Literal("-p"));
            arguments.Add(ProcessArgument.Literal(port.ToString(CultureInfo.InvariantCulture)));
        }

        arguments.Add(ProcessArgument.Literal("--"));
        arguments.Add(ProcessArgument.Native(target.SshDestination!));
        arguments.Add(ProcessArgument.Literal("sh"));
        arguments.Add(ProcessArgument.Literal("-s"));
        await using var promptOperation = _credentialPromptBroker.StartOperation(
            "SSH remote repository initialization",
            cancellationToken);
        var invocation = new ProcessInvocation(
            ssh,
            arguments.ToImmutable(),
            workingDirectory,
            promptOperation.ConfigureEnvironment(_environmentFactory.CreateTransportEnvironment()),
            StandardInputSource.FromBytes(standardInput),
            OutputPolicy.Create(MaximumOutputBytes, MaximumErrorBytes));
        return await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private static RemoteInitializationTarget CanonicalizeAvailableLocalTarget(
        RemoteInitializationTarget target)
    {
        var path = Path.GetFullPath(target.LocalPath!);
        var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var parentPath = Path.GetDirectoryName(path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(leaf) || string.IsNullOrEmpty(parentPath))
        {
            throw new RemoteInitializationException(
                "The local initialization target must name a new directory below an existing parent.");
        }

        var parent = CanonicalDirectory.Create(parentPath);
        var canonicalParent = OperatingSystem.IsWindows()
            ? parent.GetWindowsPath()
            : s_strictUtf8.GetString(parent.GetUnixBytes());
        var canonicalTarget = Path.Combine(canonicalParent, leaf);
        if (Path.Exists(canonicalTarget) ||
            new FileInfo(canonicalTarget).LinkTarget is not null ||
            new DirectoryInfo(canonicalTarget).LinkTarget is not null)
        {
            throw new RemoteInitializationException(
                "Remote initialization refuses to reuse an existing file, directory, or symbolic link.");
        }

        return new RemoteInitializationTarget(
            target.Url,
            RemoteInitializationKind.Local,
            canonicalTarget,
            sshDestination: null,
            sshPort: null,
            remotePath: null);
    }

    private static byte[] BuildInitializationInput(
        ReadOnlySpan<byte> remotePath,
        RepositoryObjectFormat objectFormat)
    {
        var payload = Convert.ToBase64String(remotePath)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        if (payload.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidDataException("The base64url path payload contains an invalid character.");
        }

        return s_strictUtf8.GetBytes(
            $"{InitializationScriptPrefix}\nGITSAIL_INIT_FRAME_V1\n" +
            $"{FormatObjectFormat(objectFormat)}\n{payload}\n" +
            $"GITSAIL.FRAME\n{InitializationScriptSuffix}\n");
    }

    private static ImmutableArray<RemoteUrl> ParseEffectiveUrls(ReadOnlySpan<byte> output)
    {
        var urls = ImmutableArray.CreateBuilder<RemoteUrl>();
        while (!output.IsEmpty)
        {
            var terminator = output.IndexOf((byte)'\n');
            if (terminator < 0)
            {
                throw new InvalidDataException("Git effective push URL output ended before a line terminator.");
            }

            var value = output[..terminator];
            if (!value.IsEmpty && value[^1] == (byte)'\r')
            {
                value = value[..^1];
            }

            urls.Add(RemoteUrl.FromBytes(value));
            output = output[(terminator + 1)..];
        }

        return urls.ToImmutable();
    }

    private static ProcessArgument CreatePrefixedPathArgument(
        ReadOnlySpan<byte> prefix,
        GitPath path)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProcessArgument.Literal(
                $"{Encoding.ASCII.GetString(prefix)}{path.GetWindowsPath()}");
        }

        var value = new byte[prefix.Length + path.GetUnixBytes().Length];
        prefix.CopyTo(value);
        path.GetUnixBytes().CopyTo(value.AsSpan(prefix.Length));
        return ProcessArgument.FromUnixBytes(value);
    }

    private static GitPath CreateNativePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(s_strictUtf8.GetBytes(path));

    private static unsafe void CreateLocalDirectoryExclusive(string path)
    {
        bool succeeded;
        if (OperatingSystem.IsWindows())
        {
            succeeded = WindowsNative.CreateDirectory(path, securityAttributes: 0) != 0;
        }
        else
        {
            var bytes = s_strictUtf8.GetBytes(path);
            var terminatedPath = new byte[bytes.Length + 1];
            bytes.CopyTo(terminatedPath, 0);
            fixed (byte* pathPointer = terminatedPath)
            {
                succeeded = UnixNative.MakeDirectory(pathPointer, mode: 0x1C0) == 0;
            }
        }

        if (!succeeded)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new RemoteInitializationException(
                "The local initialization target could not be reserved exclusively; " +
                "it may have been created after confirmation.",
                new Win32Exception(error));
        }
    }

    private static string FormatObjectFormat(RepositoryObjectFormat objectFormat)
        => objectFormat switch
        {
            RepositoryObjectFormat.Sha1 => "sha1",
            RepositoryObjectFormat.Sha256 => "sha256",
            _ => throw new ArgumentOutOfRangeException(nameof(objectFormat)),
        };

    private static bool TargetsMatch(
        RemoteInitializationTarget expected,
        RemoteInitializationTarget actual)
        => expected.Kind == actual.Kind &&
            expected.Url.Equals(actual.Url) &&
            string.Equals(expected.LocalPath, actual.LocalPath, PathComparison) &&
            Equals(expected.SshDestination, actual.SshDestination) &&
            expected.SshPort == actual.SshPort &&
            ((expected.RemotePath is null && actual.RemotePath is null) ||
                (expected.RemotePath is not null && actual.RemotePath is not null &&
                    expected.RemotePath.AsSpan().SequenceEqual(actual.RemotePath)));

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static GitCommandException CreateCommandException(
        ProcessResult result,
        string fallbackError,
        RemoteUrl sensitiveUrl)
        => CreateCommandException(result, fallbackError, [sensitiveUrl]);

    private static GitCommandException CreateCommandException(
        ProcessResult result,
        string fallbackError,
        IReadOnlyList<RemoteUrl> sensitiveUrls)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        if (string.IsNullOrEmpty(error))
        {
            error = Encoding.UTF8.GetString(result.StandardOutput.Span).Trim();
        }

        foreach (var url in sensitiveUrls)
        {
            error = url.RedactFrom(error);
        }

        return new GitCommandException(
            result.ExitCode,
            string.IsNullOrEmpty(error) ? fallbackError : error);
    }
}
