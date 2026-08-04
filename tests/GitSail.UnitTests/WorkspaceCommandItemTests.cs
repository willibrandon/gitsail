using GitSail.Testing;
using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies stable searchable command metadata and honest unavailable presentation.
/// </summary>
[TestClass]
public sealed class WorkspaceCommandItemTests
{
    private static readonly string[] s_expectedMenuCategories = ["Help", "Tools"];

    /// <summary>
    /// Verifies every presented field participates in case-insensitive palette matching.
    /// </summary>
    [TestMethod]
    public void Matches_WithActionMetadata_SearchesEveryPresentedField()
    {
        var item = new WorkspaceCommandItem(
            "merge.abort",
            "Merge",
            "Abort merge",
            "Ask Git to abort the verified merge.",
            "Ctrl+K",
            "No active merge.",
            _ => Task.CompletedTask);

        Assert.IsTrue(item.Matches("MERGE.ABORT"));
        Assert.IsTrue(item.Matches("merge"));
        Assert.IsTrue(item.Matches("abort"));
        Assert.IsTrue(item.Matches("verified"));
        Assert.IsTrue(item.Matches("ctrl+k"));
        Assert.IsTrue(item.Matches("active"));
        Assert.IsFalse(item.Matches("stash"));
    }

    /// <summary>
    /// Verifies unavailable commands remain visible with binding and status markers.
    /// </summary>
    [TestMethod]
    public void ToString_WhenUnavailable_PreservesActionBindingAndStatus()
    {
        var item = new WorkspaceCommandItem(
            "repository.refresh",
            "Repository",
            "Refresh",
            "Refresh repository state.",
            "F5",
            "Another operation is running.",
            _ => Task.CompletedTask);

        Assert.IsFalse(item.IsAvailable);
        Assert.AreEqual("Repository: Refresh [F5] [unavailable]", item.ToString());
    }

    /// <summary>
    /// Verifies one action can appear in useful menus without duplicating its identity or executor.
    /// </summary>
    [TestMethod]
    public void MenuCategories_WithSharedAction_RemainSearchableAndOrdered()
    {
        var item = new WorkspaceCommandItem(
            "help.doctor",
            "Help",
            "Doctor and runtime",
            "Inspect runtime capabilities.",
            string.Empty,
            unavailableReason: null,
            _ => Task.CompletedTask,
            ["Help", "Tools"]);

        TestSeq.AreEqual(s_expectedMenuCategories, item.MenuCategories);
        Assert.IsTrue(item.Matches("tools"));
        Assert.AreEqual("Help: Doctor and runtime", item.ToString());
    }
}
