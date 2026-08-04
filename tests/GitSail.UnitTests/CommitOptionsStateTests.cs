using GitSail.Domain;
using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies lifted commit-option toggles, cleanup ordering, identity inputs, and immutable requests.
/// </summary>
[TestClass]
public sealed class CommitOptionsStateTests
{
    /// <summary>
    /// Verifies every lifted option is represented exactly in the resulting commit request.
    /// </summary>
    [TestMethod]
    public void CreateRequest_WithSelectedOptions_ReturnsCompleteTransactionRequest()
    {
        var state = new CommitOptionsState(amend: true);
        state.ToggleExpanded();
        state.ToggleSignoff();
        state.ToggleSignCommit();
        state.CycleCleanupMode();
        state.Author.Text = "  Example Author <author@example.invalid>  ";
        state.SigningKey.Text = "  signing-key  ";

        var request = state.CreateRequest("subject\n", skipHooks: true);

        Assert.IsTrue(state.IsExpanded);
        Assert.IsTrue(request.Amend);
        Assert.IsTrue(request.Signoff);
        Assert.IsTrue(request.SignCommit);
        Assert.IsTrue(request.SkipHooks);
        Assert.AreEqual(CommitCleanupMode.Strip, request.CleanupMode);
        Assert.AreEqual("Example Author <author@example.invalid>", request.Author);
        Assert.AreEqual("signing-key", request.SigningKey);
        Assert.AreEqual("subject\n", request.Message);
    }

    /// <summary>
    /// Verifies cleanup cycling covers every documented mode and returns to the default.
    /// </summary>
    [TestMethod]
    public void CycleCleanupMode_AcrossCompleteSet_ReturnsToDefault()
    {
        var state = new CommitOptionsState(amend: false);
        var observed = new List<CommitCleanupMode>();

        for (var index = 0; index < 5; index++)
        {
            state.CycleCleanupMode();
            observed.Add(state.CleanupMode);
        }

        CollectionAssert.AreEqual(
            new[]
            {
                CommitCleanupMode.Strip,
                CommitCleanupMode.Whitespace,
                CommitCleanupMode.Verbatim,
                CommitCleanupMode.Scissors,
                CommitCleanupMode.Default,
            },
            observed);
    }

    /// <summary>
    /// Verifies optional blank identity inputs remain absent instead of becoming whitespace arguments.
    /// </summary>
    [TestMethod]
    public void CreateRequest_WithBlankOptionalInputs_LeavesOverridesAbsent()
    {
        var state = new CommitOptionsState(amend: false);
        state.Author.Text = "   ";
        state.SigningKey.Text = "\t";

        var request = state.CreateRequest("subject");

        Assert.IsNull(request.Author);
        Assert.IsNull(request.SigningKey);
    }
}
