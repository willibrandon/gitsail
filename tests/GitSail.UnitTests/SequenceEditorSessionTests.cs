using GitSail.Domain;
using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies bounded focus, typed edits, and exec trust in the sequence-editor session.
/// </summary>
[TestClass]
public sealed class SequenceEditorSessionTests
{
    /// <summary>
    /// Verifies focus and command movement stop at both bounds without wrapping.
    /// </summary>
    [TestMethod]
    public void MoveFocusAndCommand_AtBounds_DoesNotWrap()
    {
        var session = CreateSession();

        session.MoveFocus(-1);
        Assert.AreEqual(0, session.FocusedIndex);
        session.MoveCommand(-1);
        Assert.AreEqual(0, session.FocusedIndex);
        session.MoveCommand(1);
        Assert.AreEqual(2, session.FocusedIndex);
        session.MoveCommand(1);
        Assert.AreEqual(2, session.FocusedIndex);
        Assert.AreEqual("pick 1111 one", session.Document.Entries[2].DisplayText);
    }

    /// <summary>
    /// Verifies an invalid first squash is rejected and the prior action is restored.
    /// </summary>
    [TestMethod]
    public void ChangeAction_WithFirstSquash_RestoresValidPriorAction()
    {
        var session = CreateSession();

        session.ChangeAction(RebaseTodoAction.Squash);

        Assert.AreEqual(RebaseTodoAction.Pick, session.FocusedEntry?.Action);
        StringAssert.Contains(session.Status, "earlier commit", StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a parsed exec command blocks save until the user explicitly trusts it.
    /// </summary>
    [TestMethod]
    public void TrySave_WithUntrustedExec_RequiresExplicitTrust()
    {
        var session = new SequenceEditorSession(RebaseTodoParser.Parse(
            "pick 1111 one\nexec dotnet test\n"u8));

        Assert.IsFalse(session.TrySave());
        Assert.IsFalse(session.IsSaved);
        session.TrustExecCommands();
        Assert.IsTrue(session.TrySave());
        Assert.IsTrue(session.IsSaved);
    }

    /// <summary>
    /// Verifies adding and removing a trusted exec command retains a valid plan.
    /// </summary>
    [TestMethod]
    public void InsertTrustedExec_ThenRemove_RestoresOriginalPlan()
    {
        var session = CreateSession();

        session.InsertTrustedExec("dotnet test --no-restore");

        Assert.AreEqual(RebaseTodoAction.Exec, session.FocusedEntry?.Action);
        Assert.IsTrue(session.ExecCommandsTrusted);
        session.RemoveExec();
        Assert.IsFalse(session.HasExecCommands);
        Assert.IsTrue(session.TrySave());
    }

    private static SequenceEditorSession CreateSession()
        => new(RebaseTodoParser.Parse(
            "pick 1111 one\n# divider\npick 2222 two\n"u8));
}
