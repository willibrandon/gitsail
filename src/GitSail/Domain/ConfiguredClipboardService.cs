using GitSail.Git.Execution;
using GitSail.Ui;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Domain;

/// <summary>
/// Enforces live Git clipboard configuration across terminal and platform-helper boundaries.
/// </summary>
internal sealed class ConfiguredClipboardService : IClipboardService
{
    private const int MaximumClipboardBytes = 16 * 1024 * 1024;
    private readonly Func<GitConfigurationSnapshot?> _configurationProvider;
    private readonly ExecutableResolver _resolver;
    private readonly IChildProcessRunner _runner;
    private readonly IProcessEnvironment _environment;
    private readonly CanonicalDirectory _workingDirectory;

    /// <summary>
    /// Initializes a configuration-driven clipboard boundary over trusted process services.
    /// </summary>
    /// <param name="configurationProvider">Supplies the latest complete Git configuration snapshot.</param>
    /// <param name="environment">Supplies classified startup environment values.</param>
    /// <param name="runner">Executes a resolved helper without a shell.</param>
    /// <param name="workingDirectory">Supplies a canonical existing child working directory.</param>
    internal ConfiguredClipboardService(
        Func<GitConfigurationSnapshot?> configurationProvider,
        IProcessEnvironment environment,
        IChildProcessRunner runner,
        CanonicalDirectory workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(configurationProvider);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        _configurationProvider = configurationProvider;
        _environment = environment;
        _resolver = new ExecutableResolver(environment);
        _runner = runner;
        _workingDirectory = workingDirectory;
    }

    /// <inheritdoc />
    public async Task<ClipboardCopyResult> CopyAsync(
        string text,
        ClipboardContentClassification classification,
        Action<string> sendOsc52,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sendOsc52);
        cancellationToken.ThrowIfCancellationRequested();
        if (classification == ClipboardContentClassification.Secret)
        {
            return new ClipboardCopyResult(
                Succeeded: false,
                Confirmed: false,
                "Secret values cannot be copied to the clipboard.");
        }

        var byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount > MaximumClipboardBytes)
        {
            return new ClipboardCopyResult(
                Succeeded: false,
                Confirmed: false,
                $"Clipboard content exceeds the {MaximumClipboardBytes / (1024 * 1024)} MiB safety limit.");
        }

        var configuration = _configurationProvider() ?? new GitConfigurationSnapshot([]);
        var policy = ClipboardPolicyResolver.Resolve(configuration);
        if (policy == ClipboardPolicy.Off)
        {
            return new ClipboardCopyResult(
                Succeeded: false,
                Confirmed: false,
                "Clipboard copying is disabled by gitsail.clipboard=off or an invalid value.");
        }

        if (policy == ClipboardPolicy.Osc52)
        {
            return SendOsc52(text, sendOsc52);
        }

        try
        {
            var helperResult = await CopyWithHelperAsync(text, cancellationToken).ConfigureAwait(false);
            if (policy != ClipboardPolicy.Auto || helperResult.Succeeded)
            {
                return helperResult;
            }

            var fallback = SendOsc52(text, sendOsc52);
            return fallback with
            {
                Message = $"{helperResult.Message} {fallback.Message}",
            };
        }
        catch (ExecutableResolutionException) when (policy == ClipboardPolicy.Auto)
        {
            return SendOsc52(text, sendOsc52);
        }
        catch (ExecutableResolutionException exception)
        {
            return Failed($"Clipboard helper unavailable: {exception.Message}");
        }
        catch (IOException) when (policy == ClipboardPolicy.Auto)
        {
            var fallback = SendOsc52(text, sendOsc52);
            return fallback with
            {
                Message = $"Clipboard helper failed; {fallback.Message}",
            };
        }
        catch (IOException exception)
        {
            return Failed($"Clipboard helper failed: {TerminalTextSanitizer.Sanitize(exception.Message)}");
        }
        catch (UnauthorizedAccessException) when (policy == ClipboardPolicy.Auto)
        {
            var fallback = SendOsc52(text, sendOsc52);
            return fallback with
            {
                Message = $"Clipboard helper was denied; {fallback.Message}",
            };
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failed($"Clipboard helper was denied: {TerminalTextSanitizer.Sanitize(exception.Message)}");
        }
    }

    private async Task<ClipboardCopyResult> CopyWithHelperAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var executable = _resolver.Resolve(ProgramKind.Clipboard);
        var helperName = Path.GetFileNameWithoutExtension(executable.Path);
        var invocation = new ProcessInvocation(
            executable,
            GetArguments(helperName),
            _workingDirectory,
            CreateEnvironment(),
            StandardInputSource.FromBytes(GetInputBytes(helperName, text)),
            OutputPolicy.Create(4 * 1024, 4 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = result.StandardError.IsEmpty
                ? $"exit code {result.ExitCode}"
                : TerminalTextSanitizer.Sanitize(Encoding.UTF8.GetString(result.StandardError.Span));
            return Failed($"Clipboard helper {helperName} failed: {detail}");
        }

        return new ClipboardCopyResult(
            Succeeded: true,
            Confirmed: true,
            $"Copied to the clipboard with {helperName}.");
    }

    private static ClipboardCopyResult SendOsc52(string text, Action<string> sendOsc52)
    {
        try
        {
            sendOsc52(text);
            return new ClipboardCopyResult(
                Succeeded: true,
                Confirmed: false,
                "Sent an OSC 52 clipboard request; the terminal did not confirm whether it was accepted.");
        }
        catch (IOException exception)
        {
            return Failed($"OSC 52 clipboard request failed: {TerminalTextSanitizer.Sanitize(exception.Message)}");
        }
    }

    private static ClipboardCopyResult Failed(string message)
        => new(Succeeded: false, Confirmed: false, message);

    private static ImmutableArray<ProcessArgument> GetArguments(string helperName)
        => helperName.ToLowerInvariant() switch
        {
            "wl-copy" =>
            [
                ProcessArgument.Literal("--type"),
                ProcessArgument.Literal("text/plain;charset=utf-8"),
            ],
            "xclip" =>
            [
                ProcessArgument.Literal("-selection"),
                ProcessArgument.Literal("clipboard"),
                ProcessArgument.Literal("-in"),
            ],
            "xsel" =>
            [
                ProcessArgument.Literal("--clipboard"),
                ProcessArgument.Literal("--input"),
            ],
            _ => [],
        };

    private static byte[] GetInputBytes(string helperName, string text)
        => string.Equals(helperName, "clip", StringComparison.OrdinalIgnoreCase)
            ? [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]
            : Encoding.UTF8.GetBytes(text);

    private ChildEnvironment CreateEnvironment()
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        CopyIfPresent(variables, "HOME");
        CopyIfPresent(variables, "USERPROFILE");
        CopyIfPresent(variables, "TMPDIR");
        CopyIfPresent(variables, "TEMP");
        CopyIfPresent(variables, "TMP");
        CopyIfPresent(variables, "LANG");
        CopyIfPresent(variables, "LC_ALL");
        CopyIfPresent(variables, "LC_CTYPE");
        CopyIfPresent(variables, "XDG_RUNTIME_DIR");
        CopyIfPresent(variables, "WAYLAND_DISPLAY");
        CopyIfPresent(variables, "DISPLAY");
        return ChildEnvironment.Create(variables);
    }

    private void CopyIfPresent(Dictionary<string, string> variables, string name)
    {
        var value = _environment.GetVariable(name);
        if (value is not null)
        {
            variables.Add(name, value);
        }
    }
}
