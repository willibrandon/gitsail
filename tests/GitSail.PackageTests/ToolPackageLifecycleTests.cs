using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GitSail.PackageTests;

/// <summary>
/// Verifies staged .NET tool packages through clean installation and lifecycle operations.
/// </summary>
[TestClass]
public sealed class ToolPackageLifecycleTests
{
    /// <summary>
    /// Verifies the MTP host stays on CoreCLR and loads the package application's managed identity.
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
    /// Verifies hashes, manifests, RID selection, global and local lifecycles, help, and completions.
    /// </summary>
    [TestMethod]
    public async Task StagedPackages_WithHostRid_PassCompleteToolLifecycle()
    {
        var repositoryRoot = Environment.GetEnvironmentVariable("GITSAIL_PACKAGE_REPOSITORY_ROOT");
        var packageDirectory = Environment.GetEnvironmentVariable("GITSAIL_PACKAGE_DIRECTORY");
        var evidenceDirectory = Environment.GetEnvironmentVariable("GITSAIL_PACKAGE_EVIDENCE_DIRECTORY");
        var expectedRid = Environment.GetEnvironmentVariable("GITSAIL_PACKAGE_RID");
        if (repositoryRoot is null &&
            packageDirectory is null &&
            evidenceDirectory is null &&
            expectedRid is null)
        {
            Assert.Inconclusive(
                "The ordinary CoreCLR suite does not have staged tool packages. " +
                "Native release lanes run this test again against the packages for their host RID.");
        }

        Assert.IsFalse(string.IsNullOrWhiteSpace(repositoryRoot));
        Assert.IsFalse(string.IsNullOrWhiteSpace(packageDirectory));
        Assert.IsFalse(string.IsNullOrWhiteSpace(evidenceDirectory));
        Assert.IsFalse(string.IsNullOrWhiteSpace(expectedRid));
        var requiredRepositoryRoot = repositoryRoot!;
        var requiredPackageDirectory = packageDirectory!;
        var requiredEvidenceDirectory = evidenceDirectory!;
        var requiredRid = expectedRid!;
        Assert.IsTrue(Path.IsPathFullyQualified(requiredRepositoryRoot));
        Assert.IsTrue(Path.IsPathFullyQualified(requiredPackageDirectory));
        Assert.IsTrue(Path.IsPathFullyQualified(requiredEvidenceDirectory));
        Assert.AreEqual(requiredRid, RuntimeInformation.RuntimeIdentifier);
        Assert.IsTrue(File.Exists(Path.Combine(requiredRepositoryRoot, "GitSail.slnx")));
        Assert.IsTrue(Directory.Exists(requiredPackageDirectory));
        Assert.IsTrue(Directory.Exists(requiredEvidenceDirectory));

        var result = await RunAsync(
            "dotnet",
            [
                "run",
                "--file",
                Path.Combine(requiredRepositoryRoot, "eng/verify-tool-package.cs"),
                "--",
                "--rid",
                requiredRid,
                "--package-directory",
                requiredPackageDirectory,
                "--evidence-directory",
                requiredEvidenceDirectory,
            ],
            requiredRepositoryRoot,
            TestContext.Current!.CancellationToken);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        StringAssert.Contains(
            result.StandardOutput,
            $"Verified the complete staged GitSail {BuildInformation.Version} package lifecycle for {requiredRid}.");
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
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
        Assert.IsTrue(process.Start(), $"Could not start process '{fileName}'.");
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
