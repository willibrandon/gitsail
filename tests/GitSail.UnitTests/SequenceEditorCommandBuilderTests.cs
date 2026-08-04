using GitSail.Git.Execution;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies safe Git-shell command construction for the current sequence-editor helper.
/// </summary>
[TestClass]
public sealed class SequenceEditorCommandBuilderTests
{
    /// <summary>
    /// Verifies a native executable path and hidden subcommand are independently shell quoted.
    /// </summary>
    [TestMethod]
    public void Build_WithNativeExecutable_QuotesExecutableAndSubcommand()
    {
        var processPath = Path.GetFullPath(Path.Combine("tools", "git sail'preview"));

        var command = SequenceEditorCommandBuilder.Build(processPath, [processPath]);

        Assert.AreEqual(
            $"'{processPath.Replace("'", "'\\''", StringComparison.Ordinal)}' 'sequence-editor' '--'",
            command);
    }

    /// <summary>
    /// Verifies a framework-dependent invocation retains the exact managed assembly path.
    /// </summary>
    [TestMethod]
    public void Build_WithDotNetHost_IncludesManagedAssemblyBeforeSubcommand()
    {
        var processPath = Path.GetFullPath(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var assemblyPath = Path.GetFullPath(Path.Combine("app", "git-tui.dll"));

        var command = SequenceEditorCommandBuilder.Build(processPath, [assemblyPath]);

        Assert.AreEqual($"'{processPath}' '{assemblyPath}' 'sequence-editor' '--'", command);
    }
}
