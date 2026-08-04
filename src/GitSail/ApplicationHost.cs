using GitSail.CommandLine;
using GitSail.Git.Execution;
using System.CommandLine;
using System.Text;

namespace GitSail;

/// <summary>
/// Owns process-level command-line execution and exit-code policy.
/// </summary>
internal static class ApplicationHost
{
    /// <summary>
    /// Parses and invokes one GitSail command.
    /// </summary>
    /// <param name="arguments">The managed command-line arguments.</param>
    /// <param name="cancellationToken">Signals graceful application cancellation.</param>
    /// <returns>The documented process exit code.</returns>
    internal static async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var processEnvironment = new RuntimeProcessEnvironment();
        if (CredentialPromptHelperInvocation.IsRequested(processEnvironment))
        {
            try
            {
                return await CredentialPromptHelperClient.RunAsync(
                    arguments,
                    processEnvironment,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or
                IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                return ExitCodes.Failure;
            }
        }

        var commandLine = new GitSailCommandLine(cancellationToken);
        var rootCommand = commandLine.CreateRootCommand();
        var parseResult = rootCommand.Parse(arguments, new ParserConfiguration
        {
            EnablePosixBundling = false,
            ResponseFileTokenReplacer = null,
        });

        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                await Console.Error.WriteLineAsync(error.Message).ConfigureAwait(false);
            }

            return ExitCodes.Usage;
        }

        try
        {
            return await parseResult.InvokeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Cancelled;
        }
    }
}
