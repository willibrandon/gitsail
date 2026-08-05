using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hex1b;
using Hex1b.Automation;

namespace GitSail.PerformanceTests;

/// <summary>
/// Enforces release performance budgets against the actual stripped Native AOT application.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NativeAotPerformanceTests
{
    private const string EnforceTimingBudgetsVariable =
        "GITSAIL_PERFORMANCE_ENFORCE_TIMING_BUDGETS";
    private const int WarmVersionSampleCount = 40;
    private const int WarmFrameSampleCount = 20;
    private const double WarmVersionP95BudgetMilliseconds = 25;
    private const double WarmFrameP95BudgetMilliseconds = 100;
    private const long X64ExecutableSizeBudgetBytes = 40L * 1024 * 1024;
    private const int UnixSigKill = 9;

    /// <summary>
    /// Verifies the performance test host remains a diagnostic and coverage-capable CoreCLR process.
    /// </summary>
    [TestMethod]
    public void ManagedHost_WithProductReference_LoadsExpectedBuildIdentity()
    {
        Assert.IsTrue(RuntimeFeature.IsDynamicCodeSupported);
        Assert.AreEqual("git-tui", typeof(BuildInformation).Assembly.GetName().Name);
        StringAssert.StartsWith(BuildInformation.DisplayVersion, "GitSail ");
    }

    /// <summary>
    /// Verifies warm version invocations meet the twenty-five millisecond P95 release budget.
    /// </summary>
    [TestMethod]
    public async Task PublishedPayload_WithWarmVersionInvocations_MeetsP95Budget()
    {
        var (executable, _) = GetPublishedPayloadOrSkip();
        var cancellationToken = TestContext.Current!.CancellationToken;
        for (var index = 0; index < 5; index++)
        {
            _ = await RunProcessAsync(
                executable,
                ["--version"],
                Directory.GetCurrentDirectory(),
                cancellationToken);
        }

        var samples = new double[WarmVersionSampleCount];
        for (var index = 0; index < samples.Length; index++)
        {
            var result = await RunProcessAsync(
                executable,
                ["--version"],
                Directory.GetCurrentDirectory(),
                cancellationToken);
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            StringAssert.Contains(result.StandardOutput, BuildInformation.DisplayVersion);
            samples[index] = result.Elapsed.TotalMilliseconds;
        }

        var p95 = GetPercentile(samples, 0.95);
        Console.WriteLine(
            $"GitSail performance: warm --version P95={p95:F3} ms; " +
            $"budget={WarmVersionP95BudgetMilliseconds:F0} ms; samples={FormatSamples(samples)}");
        AssertTimingBudget(
            WarmVersionP95BudgetMilliseconds,
            p95,
            $"Warm --version P95 was {p95:F3} ms; the release budget is " +
            $"{WarmVersionP95BudgetMilliseconds:F0} ms. Samples: {FormatSamples(samples)}");
    }

    /// <summary>
    /// Verifies the stripped x64 executable stays within the forty MiB release budget.
    /// </summary>
    [TestMethod]
    public void PublishedPayload_WithSupportedArchitecture_MeetsApplicableSizeBudget()
    {
        var (executable, rid) = GetPublishedPayloadOrSkip();
        if (!rid.EndsWith("-x64", StringComparison.Ordinal))
        {
            Console.WriteLine($"GitSail performance: {rid} has no x64 executable-size gate.");
            return;
        }

        var size = new FileInfo(executable).Length;
        Console.WriteLine(
            $"GitSail performance: {rid} stripped executable={size:N0} bytes; " +
            $"budget={X64ExecutableSizeBudgetBytes:N0} bytes.");
        Assert.IsLessThanOrEqualTo(
            X64ExecutableSizeBudgetBytes,
            size,
            $"The stripped {rid} executable is {size:N0} bytes; the release budget is " +
            $"{X64ExecutableSizeBudgetBytes:N0} bytes.");
    }

    /// <summary>
    /// Verifies a warm repository reaches its first complete interactive frame within the P95 budget.
    /// </summary>
    [TestMethod]
    public async Task PublishedPayload_WithWarmRepository_MeetsFirstInteractiveFrameP95Budget()
    {
        var (executable, _) = GetPublishedPayloadOrSkip();
        var cancellationToken = TestContext.Current!.CancellationToken;
        var repository = await CreateRepositoryAsync(cancellationToken);
        try
        {
            for (var index = 0; index < 3; index++)
            {
                _ = await MeasureFirstFrameAsync(executable, repository, cancellationToken);
            }

            var samples = new double[WarmFrameSampleCount];
            for (var index = 0; index < samples.Length; index++)
            {
                samples[index] = (await MeasureFirstFrameAsync(
                    executable,
                    repository,
                    cancellationToken)).TotalMilliseconds;
            }

            var p95 = GetPercentile(samples, 0.95);
            Console.WriteLine(
                $"GitSail performance: warm first interactive frame P95={p95:F3} ms; " +
                $"budget={WarmFrameP95BudgetMilliseconds:F0} ms; samples={FormatSamples(samples)}");
            AssertTimingBudget(
                WarmFrameP95BudgetMilliseconds,
                p95,
                $"Warm first-frame P95 was {p95:F3} ms; the release budget is " +
                $"{WarmFrameP95BudgetMilliseconds:F0} ms. Samples: {FormatSamples(samples)}");
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    private static (string Executable, string Rid) GetPublishedPayloadOrSkip()
    {
        var publishDirectory = Environment.GetEnvironmentVariable(
            "GITSAIL_PERFORMANCE_PUBLISH_DIRECTORY");
        var expectedRid = Environment.GetEnvironmentVariable("GITSAIL_PERFORMANCE_RID");
        if (publishDirectory is null && expectedRid is null)
        {
            Assert.Inconclusive(
                "The ordinary CoreCLR suite does not have a release Native AOT payload. " +
                "Native release lanes run the performance gates against their published executable.");
        }

        Assert.IsFalse(string.IsNullOrWhiteSpace(publishDirectory));
        Assert.IsFalse(string.IsNullOrWhiteSpace(expectedRid));
        var requiredPublishDirectory = publishDirectory!;
        var requiredRid = expectedRid!;
        Assert.IsTrue(Path.IsPathFullyQualified(requiredPublishDirectory));
        Assert.AreEqual(requiredRid, RuntimeInformation.RuntimeIdentifier);
        var executable = Path.Combine(
            requiredPublishDirectory,
            OperatingSystem.IsWindows() ? "git-tui.exe" : "git-tui");
        Assert.IsTrue(File.Exists(executable), $"The Native AOT executable is missing: {executable}");
        return (executable, requiredRid);
    }

    private static async Task<string> CreateRepositoryAsync(CancellationToken cancellationToken)
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            $"gitsail-performance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repository);
        try
        {
            var init = await RunProcessAsync(
                "git",
                ["init", "--quiet", "--initial-branch=main", repository],
                Directory.GetCurrentDirectory(),
                cancellationToken);
            Assert.AreEqual(0, init.ExitCode, init.StandardError);
            await File.WriteAllTextAsync(
                Path.Combine(repository, "tracked.txt"),
                "performance fixture\n",
                cancellationToken);
            var add = await RunProcessAsync(
                "git",
                ["-C", repository, "add", "--", "tracked.txt"],
                Directory.GetCurrentDirectory(),
                cancellationToken);
            Assert.AreEqual(0, add.ExitCode, add.StandardError);
            var commit = await RunProcessAsync(
                "git",
                [
                    "-C",
                    repository,
                    "-c",
                    "user.name=GitSail Performance Tests",
                    "-c",
                    "user.email=performance@gitsail.invalid",
                    "commit",
                    "--quiet",
                    "-m",
                    "performance fixture",
                ],
                Directory.GetCurrentDirectory(),
                cancellationToken);
            Assert.AreEqual(0, commit.ExitCode, commit.StandardError);
            return repository;
        }
        catch
        {
            DeleteRepository(repository);
            throw;
        }
    }

    private static void AssertTimingBudget(double upperBound, double value, string message)
    {
        var configuredValue = Environment.GetEnvironmentVariable(EnforceTimingBudgetsVariable);
        if (configuredValue is null ||
            string.Equals(configuredValue, bool.TrueString, StringComparison.OrdinalIgnoreCase))
        {
            Assert.IsLessThanOrEqualTo(upperBound, value, message);
            return;
        }

        Assert.IsTrue(
            string.Equals(configuredValue, bool.FalseString, StringComparison.OrdinalIgnoreCase),
            $"{EnforceTimingBudgetsVariable} must be 'true' or 'false'.");
        Console.WriteLine(
            "GitSail performance: timing budget recorded without enforcement because this " +
            "machine is not a pinned performance reference machine.");
    }

    private static void DeleteRepository(string repository)
    {
        if (!Directory.Exists(repository))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false,
            };
            var directory = new DirectoryInfo(repository);
            directory.Attributes &= ~FileAttributes.ReadOnly;
            foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }

        Directory.Delete(repository, recursive: true);
    }

    private static async Task<TimeSpan> MeasureFirstFrameAsync(
        string executable,
        string repository,
        CancellationToken cancellationToken)
    {
        const int width = 120;
        const int height = 30;
        var filter = new FirstInteractiveFrameFilter();
        var builder = Hex1bTerminal.CreateBuilder();
        var processWorkingDirectory = Directory.GetCurrentDirectory();
        var proxyPath = Environment.GetEnvironmentVariable("GITSAIL_PERFORMANCE_PTY_PROXY");
        Hex1bTerminalChildProcess? childProcess = null;
        PtyProxyWorkloadAdapter? proxy = null;
        if (string.IsNullOrWhiteSpace(proxyPath))
        {
            childProcess = new Hex1bTerminalChildProcess(
                executable,
                ["gui", "--working-dir", repository],
                processWorkingDirectory,
                environment: null,
                inheritEnvironment: true,
                initialWidth: width,
                initialHeight: height);
            _ = builder.WithWorkload(childProcess);
        }
        else
        {
            Assert.IsTrue(Path.IsPathFullyQualified(proxyPath));
            Assert.IsTrue(File.Exists(proxyPath), $"The test PTY proxy is missing: {proxyPath}");
            proxy = new PtyProxyWorkloadAdapter(
                proxyPath,
                executable,
                ["gui", "--working-dir", repository],
                processWorkingDirectory,
                width,
                height);
            _ = builder.WithWorkload(proxy);
        }

        await using var terminal = builder
            .WithHeadless()
            .WithDimensions(width, height)
            .AddPresentationFilter(filter)
            .Build();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var runTask = terminal.RunAsync(timeout.Token);
        Task<int> processExitTask;
        if (proxy is not null)
        {
            await proxy.StartAsync(timeout.Token);
            processExitTask = proxy.WaitForExitAsync(CancellationToken.None);
        }
        else
        {
            await childProcess!.StartAsync(timeout.Token);
            processExitTask = childProcess.WaitForExitAsync(CancellationToken.None);
        }

        TimeSpan firstFrame;
        try
        {
            var completed = await Task.WhenAny(filter.FirstFrame, processExitTask)
                .WaitAsync(timeout.Token);
            if (completed == processExitTask)
            {
                var exitCode = await processExitTask;
                var diagnostics = proxy is null
                    ? string.Empty
                    : await proxy.GetDiagnosticsAsync();
                Assert.Fail(
                    $"The Native AOT application exited with code {exitCode} before its first frame." +
                    (string.IsNullOrWhiteSpace(diagnostics)
                        ? string.Empty
                        : Environment.NewLine + diagnostics));
            }

            firstFrame = await filter.FirstFrame;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            using var snapshot = terminal.CreateSnapshot();
            Assert.Fail(
                "The first interactive frame was not detected. Terminal contents:" +
                Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    Enumerable.Range(0, snapshot.Height).Select(snapshot.GetLine)));
            throw;
        }
        finally
        {
            if (childProcess is { HasStarted: true, HasExited: false })
            {
                await TerminateChildProcessAsync(childProcess);
            }

            proxy?.Kill();
            _ = await processExitTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            timeout.Cancel();
            try
            {
                _ = await runTask;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
        }

        return firstFrame;
    }

    private static async Task TerminateChildProcessAsync(Hex1bTerminalChildProcess childProcess)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var process = Process.GetProcessById(childProcess.ProcessId);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                return;
            }
            catch (ArgumentException)
            {
                return;
            }
        }

        childProcess.Kill(UnixSigKill);
    }

    private static double GetPercentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        Assert.IsNotEmpty(ordered);
        var index = Math.Clamp(
            (int)Math.Ceiling(percentile * ordered.Length) - 1,
            0,
            ordered.Length - 1);
        return ordered[index];
    }

    private static string FormatSamples(IEnumerable<double> samples)
        => string.Join(", ", samples.Select(sample => sample.ToString("F3")));

    private static async Task<(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        TimeSpan Elapsed)> RunProcessAsync(
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
        var stopwatch = Stopwatch.StartNew();
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

        stopwatch.Stop();
        return (
            process.ExitCode,
            await standardOutput,
            await standardError,
            stopwatch.Elapsed);
    }
}
