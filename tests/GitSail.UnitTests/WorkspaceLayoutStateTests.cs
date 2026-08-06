using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies versioned pinned-menu layout parsing, mutation, and deterministic persistence.
/// </summary>
[TestClass]
public sealed class WorkspaceLayoutStateTests
{
    /// <summary>
    /// Verifies an absent layout produces one valid empty version-1 record.
    /// </summary>
    [TestMethod]
    public void TryParse_WithAbsentValue_ReturnsEmptyVersionOneLayout()
    {
        Assert.IsTrue(WorkspaceLayoutState.TryParse(null, out var state));

        Assert.IsEmpty(state.PinnedMenus);
        Assert.AreEqual("{\"version\":1,\"pinnedMenus\":[]}", state.ToJson());
    }

    /// <summary>
    /// Verifies pinned-menu changes preserve fields owned by splitter and tab layout features.
    /// </summary>
    [TestMethod]
    public void WithPinnedMenu_WithExistingLayout_PreservesUnrelatedFields()
    {
        const string input =
            "{\"version\":1,\"split\":44,\"tabs\":{\"active\":\"diff\"}," +
            "\"pinnedMenus\":[{\"id\":\"other.menu\",\"x\":2,\"y\":3,\"width\":40,\"height\":12}]}";
        Assert.IsTrue(WorkspaceLayoutState.TryParse(input, out var state));

        var updated = state.WithPinnedMenu(new PinnedMenuLayout(
            "workspace.application-menu",
            7,
            5,
            58,
            16));
        var json = updated.ToJson();

        StringAssert.Contains(json, "\"split\":44");
        StringAssert.Contains(json, "\"tabs\":{\"active\":\"diff\"}");
        Assert.IsTrue(WorkspaceLayoutState.TryParse(json, out var roundTripped));
        Assert.HasCount(2, roundTripped.PinnedMenus);
        Assert.AreEqual(
            new PinnedMenuLayout("workspace.application-menu", 7, 5, 58, 16),
            roundTripped.FindPinnedMenu("workspace.application-menu"));
    }

    /// <summary>
    /// Verifies replacing and removing one identity never duplicates or removes another menu.
    /// </summary>
    [TestMethod]
    public void WithAndWithoutPinnedMenu_WithExistingIdentity_ReconcilesByStableId()
    {
        var state = WorkspaceLayoutState.Empty
            .WithPinnedMenu(new PinnedMenuLayout("other.menu", 1, 2, 40, 12))
            .WithPinnedMenu(new PinnedMenuLayout("workspace.application-menu", 3, 4, 58, 16))
            .WithPinnedMenu(new PinnedMenuLayout("workspace.application-menu", 8, 9, 60, 18));

        Assert.HasCount(2, state.PinnedMenus);
        Assert.AreEqual(
            new PinnedMenuLayout("workspace.application-menu", 8, 9, 60, 18),
            state.FindPinnedMenu("workspace.application-menu"));

        var removed = state.WithoutPinnedMenu("workspace.application-menu");
        Assert.IsNull(removed.FindPinnedMenu("workspace.application-menu"));
        Assert.IsNotNull(removed.FindPinnedMenu("other.menu"));
    }

    /// <summary>
    /// Verifies malformed, duplicate, unsupported, and unbounded records fail closed.
    /// </summary>
    /// <param name="json">The rejected layout JSON.</param>
    [TestMethod]
    [DataRow("{\"version\":2,\"pinnedMenus\":[]}")]
    [DataRow("{\"version\":1,\"version\":1,\"pinnedMenus\":[]}")]
    [DataRow("{\"version\":1,\"pinnedMenus\":{},\"split\":44}")]
    [DataRow("{\"version\":1,\"pinnedMenus\":[{\"id\":\"bad id\",\"x\":1,\"y\":1,\"width\":58,\"height\":16}]}")]
    [DataRow("{\"version\":1,\"pinnedMenus\":[{\"id\":\"menu\",\"x\":-1,\"y\":1,\"width\":58,\"height\":16}]}")]
    [DataRow("{\"version\":1,\"pinnedMenus\":[{\"id\":\"menu\",\"x\":1,\"y\":1,\"width\":58,\"height\":16,\"unknown\":true}]}")]
    [DataRow("{\"version\":1,\"pinnedMenus\":[{\"id\":\"menu\",\"x\":1,\"y\":1,\"width\":58,\"height\":16},{\"id\":\"menu\",\"x\":2,\"y\":2,\"width\":58,\"height\":16}]}")]
    public void TryParse_WithInvalidRecord_ReturnsFalse(string json)
    {
        Assert.IsFalse(WorkspaceLayoutState.TryParse(json, out var state));
        Assert.AreSame(WorkspaceLayoutState.Empty, state);
    }
}
