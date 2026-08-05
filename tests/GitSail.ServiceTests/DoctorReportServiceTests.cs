using GitSail.Features.Doctor;
using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies diagnostic collection against real platform and Git boundaries.
/// </summary>
[TestClass]
public sealed class DoctorReportServiceTests
{
    private string? _temporaryDirectory;

    /// <summary>
    /// Creates an isolated existing home without application-owned directories.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-doctor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    /// <summary>
    /// Removes the isolated diagnostic home after each test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            TestDirectory.Delete(_temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies collection resolves Git while leaving every reported application path absent.
    /// </summary>
    [TestMethod]
    public async Task CreateAsync_WithFreshApplicationHome_ReportsWithoutMutation()
    {
        var environment = CreateEnvironment(_temporaryDirectory!);

        var report = await DoctorReportService.CreateAsync(
            environment,
            CanonicalDirectory.Create(_temporaryDirectory!),
            TestContext.Current!.CancellationToken);

        Assert.AreEqual(BuildInformation.ProductName, report.Product);
        Assert.IsTrue(report.Git.Available, report.Git.Error);
        Assert.IsTrue(report.Git.MeetsMinimumVersion);
        Assert.IsTrue(report.DotNetSdk.Available, report.DotNetSdk.Error);
        Assert.IsNotNull(report.DotNetSdk.Version);
        Assert.IsFalse(report.Repository.Available);
        Assert.AreEqual("not created", report.Storage.Configuration.Status);
        Assert.AreEqual("not created", report.Storage.Cache.Status);
        Assert.AreEqual("not created", report.Storage.State.Status);
        Assert.AreEqual("not created", report.Storage.Traces.Status);
        Assert.IsFalse(Directory.Exists(report.Storage.Configuration.Path));
        Assert.IsFalse(Directory.Exists(report.Storage.Cache.Path));
        Assert.IsFalse(Directory.Exists(report.Storage.State.Path));
        Assert.IsFalse(Directory.Exists(report.Storage.Traces.Path));
    }

    /// <summary>
    /// Verifies Windows command discovery rejects batch shims and requires the Git-discoverable executable.
    /// </summary>
    [TestMethod]
    public void GetCommandPathStatus_WithWindowsBatchShim_RequiresExecutable()
    {
        var processPath = Path.Combine(_temporaryDirectory!, "application.exe");
        var batchPath = Path.Combine(_temporaryDirectory!, "git-tui.cmd");
        var executablePath = Path.Combine(_temporaryDirectory!, "git-tui.exe");
        File.WriteAllText(processPath, string.Empty);
        File.WriteAllText(batchPath, string.Empty);
        var environment = new TestProcessEnvironment(new Dictionary<string, string?>
        {
            ["PATH"] = _temporaryDirectory,
        });

        var batchOnly = DoctorReportService.GetCommandPathStatus(
            environment,
            processPath,
            isWindows: true);

        Assert.AreEqual("current process exists; git-tui was not found on PATH", batchOnly);

        File.WriteAllText(executablePath, string.Empty);
        var executable = DoctorReportService.GetCommandPathStatus(
            environment,
            processPath,
            isWindows: true);

        Assert.AreEqual($"available on PATH at {Path.GetFullPath(executablePath)}", executable);
    }

    private static TestProcessEnvironment CreateEnvironment(string homeDirectory)
        => new(new Dictionary<string, string?>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["PATHEXT"] = Environment.GetEnvironmentVariable("PATHEXT"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
            ["HOME"] = homeDirectory,
            ["USERPROFILE"] = homeDirectory,
            ["APPDATA"] = Path.Combine(homeDirectory, "application-data"),
            ["LOCALAPPDATA"] = Path.Combine(homeDirectory, "local-application-data"),
            ["XDG_CONFIG_HOME"] = Path.Combine(homeDirectory, "configuration"),
            ["XDG_CACHE_HOME"] = Path.Combine(homeDirectory, "cache"),
            ["XDG_STATE_HOME"] = Path.Combine(homeDirectory, "state"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        });
}
