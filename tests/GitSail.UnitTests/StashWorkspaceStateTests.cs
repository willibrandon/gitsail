using GitSail.Domain;
using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies searchable stash focus and exact object identity remain controlled across refreshes.
/// </summary>
[TestClass]
public sealed class StashWorkspaceStateTests
{
    /// <summary>
    /// Verifies filtering matches subject and object text while retaining the exact focused entry.
    /// </summary>
    [TestMethod]
    public void SetFilter_WithSubjectAndObjectQueries_PreservesExactFocus()
    {
        var first = CreateStash(0, '1', "On main: first");
        var second = CreateStash(1, '2', "On main: release candidate");
        var state = new StashWorkspaceState();
        state.ApplyCatalog(CreateCatalog(first, second));
        state.Focus(1);

        state.SetFilter("release");

        Assert.HasCount(1, state.VisibleItems);
        Assert.AreSame(second, state.FocusedItem?.Stash);
        state.SetFilter(new string('2', 12));
        Assert.HasCount(1, state.VisibleItems);
        Assert.AreSame(second, state.FocusedItem?.Stash);
    }

    /// <summary>
    /// Verifies refresh follows the same object when its reflog position shifts.
    /// </summary>
    [TestMethod]
    public void ApplyCatalog_AfterNewEntryShiftsSelectors_FollowsFocusedObject()
    {
        var older = CreateStash(0, '1', "older");
        var state = new StashWorkspaceState();
        state.ApplyCatalog(CreateCatalog(older));
        var newest = CreateStash(0, '2', "newest");
        var shiftedOlder = CreateStash(1, '1', "older");

        state.ApplyCatalog(CreateCatalog(newest, shiftedOlder));

        Assert.AreEqual(1, state.FocusedIndex);
        Assert.AreEqual(shiftedOlder.ObjectId, state.FocusedItem?.Stash.ObjectId);
    }

    /// <summary>
    /// Verifies clearing stale catalogs retains filter input but removes action targets and patch text.
    /// </summary>
    [TestMethod]
    public void Clear_WithLoadedCatalog_RetainsFilterAndRemovesTargets()
    {
        var state = new StashWorkspaceState();
        state.ApplyCatalog(CreateCatalog(CreateStash(0, '1', "message")));
        state.SetFilter("message");

        state.Clear();

        Assert.AreEqual("message", state.Filter.Text);
        Assert.IsNull(state.Catalog);
        Assert.IsNull(state.FocusedItem);
        Assert.IsEmpty(state.VisibleItems);
        StringAssert.Contains(state.Preview.Document.GetText(), "Reload stashes");
    }

    private static StashCatalog CreateCatalog(params StashInfo[] entries)
    {
        var fingerprint = new byte[32];
        return new StashCatalog(
            new RepositoryPrecondition(headObjectId: null, headName: null, fingerprint),
            new RepositoryWorktreeFingerprint(fingerprint),
            [.. entries]);
    }

    private static StashInfo CreateStash(int index, char objectDigit, string message)
    {
        var objectText = new string(objectDigit, 40);
        Assert.IsTrue(ObjectId.TryParseHex(System.Text.Encoding.ASCII.GetBytes(objectText), out var objectId));
        return new StashInfo(
            index,
            objectId!,
            System.Text.Encoding.UTF8.GetBytes(message),
            DateTimeOffset.FromUnixTimeSeconds(1700000000 - index));
    }
}
