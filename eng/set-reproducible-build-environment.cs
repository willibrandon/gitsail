#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;

var repositoryRootOption = new Option<string?>("--repository-root")
{
    Description = "The GitSail repository whose source timestamp anchors deterministic build inputs.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand(
    "Exports the source revision timestamp and fixed locale settings for a GitHub Actions build.");
rootCommand.Options.Add(repositoryRootOption);
rootCommand.Validators.Add(result =>
{
    if (string.IsNullOrWhiteSpace(result.GetValue(repositoryRootOption)))
    {
        result.AddError("Option '--repository-root' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => ExportAsync(
    parseResult.GetValue(repositoryRootOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> ExportAsync(string repositoryRoot, CancellationToken cancellationToken)
{
    var fullRoot = Path.GetFullPath(repositoryRoot, Directory.GetCurrentDirectory());
    if (!File.Exists(Path.Combine(fullRoot, "GitSail.slnx")))
    {
        throw new DirectoryNotFoundException($"The GitSail repository root is invalid: {fullRoot}");
    }

    var revision = (await RunGitAsync(
        fullRoot,
        ["rev-parse", "--verify", "HEAD"],
        cancellationToken).ConfigureAwait(false)).Trim().ToLowerInvariant();
    var expectedRevision = Environment.GetEnvironmentVariable("GITHUB_SHA");
    if (!string.IsNullOrWhiteSpace(expectedRevision) &&
        !string.Equals(expectedRevision, revision, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            $"The checked-out revision '{revision}' does not match GITHUB_SHA '{expectedRevision}'.");
    }

    var timestampText = (await RunGitAsync(
        fullRoot,
        ["show", "-s", "--format=%ct", revision],
        cancellationToken).ConfigureAwait(false)).Trim();
    if (!long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) ||
        timestamp <= 0)
    {
        throw new InvalidDataException($"Git returned an invalid source timestamp: '{timestampText}'.");
    }

    var environmentFile = Environment.GetEnvironmentVariable("GITHUB_ENV");
    if (string.IsNullOrWhiteSpace(environmentFile))
    {
        throw new InvalidOperationException("GITHUB_ENV is unavailable outside a GitHub Actions job.");
    }

    var settings = string.Join(
        Environment.NewLine,
        $"SOURCE_DATE_EPOCH={timestamp}",
        "TZ=UTC",
        "LANG=C.UTF-8",
        "LC_ALL=C.UTF-8") + Environment.NewLine;
    await File.AppendAllTextAsync(
        environmentFile,
        settings,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        cancellationToken).ConfigureAwait(false);
    Console.WriteLine(
        $"Exported reproducible build settings for {revision} at SOURCE_DATE_EPOCH={timestamp}.");
    return 0;
}

static async Task<string> RunGitAsync(
    string workingDirectory,
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = new Process { StartInfo = startInfo };
    if (!process.Start())
    {
        throw new InvalidOperationException("Could not start Git.");
    }

    var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
    var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
    try
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
    catch
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        throw;
    }

    var output = await standardOutput.ConfigureAwait(false);
    var error = await standardError.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Git exited with code {process.ExitCode}.{Environment.NewLine}{output}{error}");
    }

    return output;
}
