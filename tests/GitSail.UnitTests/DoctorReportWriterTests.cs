using GitSail.Features.Doctor;
using System.Collections.Immutable;
using System.Text.Json;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies the stable human and JSON diagnostic report contracts.
/// </summary>
[TestClass]
public sealed class DoctorReportWriterTests
{
    /// <summary>
    /// Verifies JSON output retains stable fields, new diagnostics, and terminal-safe text.
    /// </summary>
    [TestMethod]
    public void Write_WithJsonReport_WritesStableSanitizedContract()
    {
        using var output = new StringWriter();

        DoctorReportWriter.Write(json: true, CreateReport(), output);

        var text = output.ToString();
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("GitSail", root.GetProperty("product").GetString());
        Assert.AreEqual("test-rid", root.GetProperty("runtimeIdentifier").GetString());
        Assert.IsTrue(root.GetProperty("nativeAot").GetBoolean());
        Assert.AreEqual("24bit", root.GetProperty("terminalCapabilities").GetProperty("color").GetString());
        Assert.AreEqual("available through system ICU", root.GetProperty("locale").GetProperty("globalization").GetString());
        Assert.AreEqual("sha256", root.GetProperty("repository").GetProperty("objectFormat").GetString());
        Assert.AreEqual("10.0.100", root.GetProperty("dotnetSdk").GetProperty("version").GetString());
        Assert.AreEqual(
            "/tools/ssh-keygen",
            root.GetProperty("sshKeygen").GetProperty("path").GetString());
        Assert.AreEqual(
            "porcelain-v2 status",
            root.GetProperty("git").GetProperty("capabilities")[0].GetProperty("name").GetString());
        Assert.AreEqual(
            "global",
            root.GetProperty("configurationSources")[0].GetProperty("scope").GetString());
        Assert.AreEqual(
            "file:<U+001B>unsafe",
            root.GetProperty("configurationSources")[0].GetProperty("origin").GetString());
    }

    /// <summary>
    /// Verifies human output identifies capability sections without configuration values.
    /// </summary>
    [TestMethod]
    public void Write_WithHumanReport_WritesUsefulSectionsWithoutValues()
    {
        using var output = new StringWriter();

        DoctorReportWriter.Write(json: false, CreateReport(), output);

        var text = output.ToString();
        StringAssert.Contains(text, "Product: GitSail 1.2.3");
        StringAssert.Contains(text, "Git: 2.50.0 (/tools/git)");
        StringAssert.Contains(text, "Repository: /work/repository");
        StringAssert.Contains(text, ".NET SDK: 10.0.100 (/tools/dotnet)");
        StringAssert.Contains(text, "SSH key generation: /tools/ssh-keygen");
        StringAssert.Contains(text, "Git configuration sources (values omitted):");
        StringAssert.Contains(text, "global: file:<U+001B>unsafe");
        Assert.DoesNotContain("secret-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
    }

    private static DoctorReport CreateReport()
        => new(
            "GitSail",
            "1.2.3",
            "test-rid",
            "Test OS",
            "TestArchitecture",
            true,
            "/tools/git-tui",
            "global .NET tool",
            "available on PATH at /tools/git-tui",
            new DoctorTerminalReport(
                "120x40",
                false,
                false,
                120,
                40,
                "24bit",
                "terminal key input",
                "enabled",
                "utf-8",
                "OSC 52; terminal support is not probed"),
            new DoctorLocaleReport(
                "en-US",
                "en-US",
                "utf-8",
                "utf-8",
                "available through system ICU"),
            new DoctorGitReport(
                true,
                "/tools/git",
                "2.50.0",
                true,
                ImmutableArray.Create(new DoctorCapabilityReport("porcelain-v2 status", true, "Git 2.11")),
                null),
            new DoctorRepositoryReport(
                true,
                "/work/repository",
                "/work/repository/.git",
                false,
                "sha256",
                "accepted by Git discovery",
                null),
            new DoctorToolReport("dotnetSdk", true, "/tools/dotnet", "10.0.100", null),
            new DoctorToolReport("ssh", true, "/tools/ssh", null, null),
            new DoctorToolReport("sshKeygen", true, "/tools/ssh-keygen", null, null),
            new DoctorStorageReport(
                new DoctorPathReport("configuration", "/home/test/config", "directory; mode 700"),
                new DoctorPathReport("cache", "/home/test/cache", "not created"),
                new DoctorPathReport("state", "/home/test/state", "not created"),
                new DoctorPathReport("traces", "/home/test/state/traces", "not created"),
                null),
            ImmutableArray.Create(new DoctorConfigurationSource("global", "file:\u001bunsafe")),
            false,
            null,
            "Use retained symbols.");
}
