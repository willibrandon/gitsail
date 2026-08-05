#:package System.CommandLine

using System.CommandLine;
using System.Runtime.InteropServices;

var ridOption = new Option<string?>("--rid")
{
    Description = "The Native AOT runtime identifier assigned to this runner.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand("Verifies that a CI runner natively matches its assigned runtime identifier.");
rootCommand.Options.Add(ridOption);
rootCommand.Validators.Add(result =>
{
    if (string.IsNullOrWhiteSpace(result.GetValue(ridOption)))
    {
        result.AddError("Option '--rid' is required.");
    }
});
rootCommand.SetAction(parseResult => Verify(parseResult.GetValue(ridOption)!));

return rootCommand.Parse(args).Invoke();

static int Verify(string rid)
{
    var expectedArchitecture = rid.EndsWith("-arm64", StringComparison.Ordinal)
        ? Architecture.Arm64
        : rid.EndsWith("-x64", StringComparison.Ordinal)
            ? Architecture.X64
            : throw new ArgumentException($"Unsupported runtime identifier architecture: {rid}", nameof(rid));
    if (RuntimeInformation.OSArchitecture != expectedArchitecture)
    {
        throw new PlatformNotSupportedException(
            $"Runtime identifier '{rid}' requires {expectedArchitecture}, but the runner is " +
            $"{RuntimeInformation.OSArchitecture}.");
    }

    var platformMatches = rid.StartsWith("win-", StringComparison.Ordinal)
        ? OperatingSystem.IsWindows()
        : rid.StartsWith("linux-", StringComparison.Ordinal)
            ? OperatingSystem.IsLinux()
            : rid.StartsWith("osx-", StringComparison.Ordinal)
                ? OperatingSystem.IsMacOS()
                : throw new ArgumentException($"Unsupported runtime identifier platform: {rid}", nameof(rid));
    if (!platformMatches)
    {
        throw new PlatformNotSupportedException(
            $"The runner operating system does not match runtime identifier '{rid}'.");
    }

    if (rid.StartsWith("linux-musl-", StringComparison.Ordinal) &&
        !Directory.EnumerateFiles("/lib", "ld-musl-*.so.1", SearchOption.TopDirectoryOnly).Any())
    {
        throw new PlatformNotSupportedException(
            $"Runtime identifier '{rid}' requires a native musl runner.");
    }

    Console.WriteLine(
        $"Runner matches {rid}: {RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}.");
    return 0;
}
