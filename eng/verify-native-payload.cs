#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;

var ridOption = new Option<string?>("--rid")
{
    Description = "The expected Native AOT runtime identifier.",
    Arity = ArgumentArity.ExactlyOne,
};
var publishDirectoryOption = new Option<string?>("--publish-directory")
{
    Description = "The directory containing the published Native AOT executable.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand("Runs a staged Native AOT payload and verifies its Doctor report.");
rootCommand.Options.Add(ridOption);
rootCommand.Options.Add(publishDirectoryOption);
rootCommand.Validators.Add(result =>
{
    if (string.IsNullOrWhiteSpace(result.GetValue(ridOption)))
    {
        result.AddError("Option '--rid' is required.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(publishDirectoryOption)))
    {
        result.AddError("Option '--publish-directory' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => VerifyAsync(
    parseResult.GetValue(ridOption)!,
    parseResult.GetValue(publishDirectoryOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> VerifyAsync(
    string rid,
    string publishDirectory,
    CancellationToken cancellationToken)
{
    var workingDirectory = Directory.GetCurrentDirectory();
    var executableName = rid.StartsWith("win-", StringComparison.Ordinal) ? "git-tui.exe" : "git-tui";
    var executablePath = Path.Combine(
        Path.GetFullPath(publishDirectory, workingDirectory),
        executableName);
    if (!File.Exists(executablePath))
    {
        throw new FileNotFoundException("The Native AOT executable is missing.", executablePath);
    }

    _ = await RunCheckedAsync(
        executablePath,
        ["--version"],
        workingDirectory,
        echoOutput: true,
        cancellationToken).ConfigureAwait(false);
    var doctorJson = await RunCheckedAsync(
        executablePath,
        ["doctor", "--json"],
        workingDirectory,
        echoOutput: false,
        cancellationToken).ConfigureAwait(false);

    using var document = JsonDocument.Parse(doctorJson);
    var root = document.RootElement;
    if (!root.TryGetProperty("nativeAot", out var nativeAot) || !nativeAot.GetBoolean())
    {
        throw new InvalidDataException("The staged executable does not identify itself as Native AOT.");
    }

    if (!root.TryGetProperty("runtimeIdentifier", out var runtimeIdentifier) ||
        !string.Equals(runtimeIdentifier.GetString(), rid, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"The staged executable Doctor report does not match runtime identifier '{rid}'.");
    }

    return 0;
}

static async Task<string> RunCheckedAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    bool echoOutput,
    CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
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
        throw new InvalidOperationException($"Could not start process '{fileName}'.");
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
    if (echoOutput && output.Length > 0)
    {
        await Console.Out.WriteAsync(output).ConfigureAwait(false);
    }

    if (error.Length > 0)
    {
        await Console.Error.WriteAsync(error).ConfigureAwait(false);
    }

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Process '{fileName}' exited with code {process.ExitCode}.{Environment.NewLine}{output}{error}");
    }

    return output;
}
