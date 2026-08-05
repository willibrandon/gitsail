using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GitSail.AotTests;

/// <summary>
/// Verifies the published stripped Native AOT application through its supported command-line contracts.
/// </summary>
[TestClass]
public sealed class NativeAotPayloadTests
{
    /// <summary>
    /// Verifies the MTP host stays on CoreCLR and loads GitSail's exact managed application identity.
    /// </summary>
    [TestMethod]
    public void ManagedHost_WithProductReference_LoadsExpectedBuildIdentity()
    {
        Assert.IsTrue(RuntimeFeature.IsDynamicCodeSupported);
        Assert.AreEqual("git-tui", typeof(BuildInformation).Assembly.GetName().Name);
        Assert.AreEqual(
            $"{BuildInformation.ProductName} {BuildInformation.Version}",
            BuildInformation.DisplayVersion);
    }

    /// <summary>
    /// Verifies both version entry points and the stable Doctor identity of the exact published RID payload.
    /// </summary>
    [TestMethod]
    public async Task PublishedPayload_WithExpectedRid_ExecutesAsNativeAotApplication()
    {
        var publishDirectory = Environment.GetEnvironmentVariable("GITSAIL_AOT_PUBLISH_DIRECTORY");
        var expectedRid = Environment.GetEnvironmentVariable("GITSAIL_AOT_RID");
        if (publishDirectory is null && expectedRid is null)
        {
            Assert.Inconclusive(
                "The ordinary CoreCLR suite does not have a published Native AOT payload. " +
                "Native release lanes run this test again against their stripped executable.");
        }

        Assert.IsFalse(string.IsNullOrWhiteSpace(publishDirectory));
        Assert.IsFalse(string.IsNullOrWhiteSpace(expectedRid));
        var requiredPublishDirectory = publishDirectory!;
        var requiredRid = expectedRid!;
        Assert.IsTrue(Path.IsPathFullyQualified(requiredPublishDirectory));
        Assert.AreEqual(requiredRid, RuntimeInformation.RuntimeIdentifier);

        var fullPublishDirectory = Path.GetFullPath(requiredPublishDirectory);
        var executableName = OperatingSystem.IsWindows() ? "git-tui.exe" : "git-tui";
        var executablePath = Path.Combine(fullPublishDirectory, executableName);
        Assert.IsTrue(File.Exists(executablePath), $"Native AOT executable is missing: {executablePath}");
        Assert.IsFalse(File.Exists(Path.Combine(fullPublishDirectory, "git-tui.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(fullPublishDirectory, "git-tui.deps.json")));
        Assert.IsFalse(File.Exists(Path.Combine(fullPublishDirectory, "git-tui.runtimeconfig.json")));
        var cancellationToken = TestContext.Current!.CancellationToken;

        var versionOption = await RunAsync(
            executablePath,
            ["--version"],
            cancellationToken);
        Assert.AreEqual(0, versionOption.ExitCode, versionOption.StandardError);
        Assert.AreEqual(BuildInformation.DisplayVersion, versionOption.StandardOutput.Trim());

        var versionCommand = await RunAsync(
            executablePath,
            ["version"],
            cancellationToken);
        Assert.AreEqual(0, versionCommand.ExitCode, versionCommand.StandardError);
        Assert.AreEqual(BuildInformation.DisplayVersion, versionCommand.StandardOutput.Trim());

        var doctor = await RunAsync(
            executablePath,
            ["doctor", "--json"],
            cancellationToken);
        Assert.AreEqual(0, doctor.ExitCode, doctor.StandardError);
        using var document = JsonDocument.Parse(doctor.StandardOutput);
        var root = document.RootElement;
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(BuildInformation.ProductName, root.GetProperty("product").GetString());
        Assert.AreEqual(BuildInformation.Version, root.GetProperty("version").GetString());
        Assert.AreEqual(requiredRid, root.GetProperty("runtimeIdentifier").GetString());
        Assert.IsTrue(root.GetProperty("nativeAot").GetBoolean());

        var reportedPath = root.GetProperty("command").GetProperty("path").GetString();
        Assert.IsNotNull(reportedPath);
        Assert.AreEqual(
            Path.GetFullPath(executablePath),
            Path.GetFullPath(reportedPath),
            OperatingSystem.IsWindows(),
            CultureInfo.InvariantCulture);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start(), $"Could not start Native AOT executable '{executablePath}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }

        return (
            process.ExitCode,
            await standardOutput,
            await standardError);
    }
}
