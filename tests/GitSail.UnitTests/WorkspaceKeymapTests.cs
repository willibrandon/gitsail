using System.Text;
using GitSail.Domain;
using GitSail.Ui;
using Hex1b.Input;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies configured workspace chords remain supported, stable, and collision-free.
/// </summary>
[TestClass]
public sealed class WorkspaceKeymapTests
{
    /// <summary>
    /// Verifies stable chord names parse to the expected key and modifier values.
    /// </summary>
    [TestMethod]
    public void TryParse_WithSupportedAndUnsupportedNames_ReturnsExactChord()
    {
        Assert.IsTrue(WorkspaceKeyChord.TryParse("Ctrl+Shift+F3", out var function));
        Assert.AreEqual(Hex1bKey.F3, function.Key);
        Assert.AreEqual(
            Hex1bModifiers.Control | Hex1bModifiers.Shift,
            function.Modifiers);
        Assert.IsTrue(WorkspaceKeyChord.TryParse("[", out var bracket));
        Assert.AreEqual(Hex1bKey.Oem4, bracket.Key);
        Assert.AreEqual(Hex1bModifiers.None, bracket.Modifiers);
        Assert.IsFalse(WorkspaceKeyChord.TryParse("Command+R", out _));
        Assert.IsFalse(WorkspaceKeyChord.TryParse("Ctrl+Ctrl+R", out _));
        Assert.IsFalse(WorkspaceKeyChord.TryParse("Alt+O", out _));
        Assert.IsFalse(WorkspaceKeyChord.TryParse("Hyper", out _));
    }

    /// <summary>
    /// Verifies baseline terminal aliases collapse to the same byte identities.
    /// </summary>
    [TestMethod]
    public void GetBaselineIdentity_WithTerminalAliases_ReturnsMatchingIdentity()
    {
        Assert.AreEqual(
            Parse("Tab").GetBaselineIdentity(),
            Parse("Ctrl+I").GetBaselineIdentity());
        Assert.AreEqual(
            Parse("Ctrl+I").GetBaselineIdentity(),
            Parse("Ctrl+Shift+I").GetBaselineIdentity());
        Assert.AreEqual(
            Parse("Enter").GetBaselineIdentity(),
            Parse("Ctrl+M").GetBaselineIdentity());
        Assert.AreEqual(
            Parse("Escape").GetBaselineIdentity(),
            Parse("Ctrl+[").GetBaselineIdentity());
    }

    /// <summary>
    /// Verifies a configured chord replaces every baseline trigger for one registered action.
    /// </summary>
    [TestMethod]
    public void TryApply_WithValidOverride_RebindsRegisteredAction()
    {
        var bindings = CreateBindings();
        var configuration = Configuration(
            "gitsail.keymap.repository.refresh",
            "F12");

        var applied = WorkspaceKeymap.TryApply(bindings, configuration, out var error);

        Assert.IsTrue(applied, error);
        var refresh = bindings.GetBindings(WorkspaceActionIds.Refresh);
        Assert.HasCount(1, refresh);
        Assert.AreEqual(Hex1bKey.F12, refresh[0].Steps[0].Key);
        Assert.AreEqual(
            "F12",
            WorkspaceKeymap.GetDisplayBinding(
                configuration,
                WorkspaceActionIds.Refresh,
                "F5 / Ctrl+R"));
    }

    /// <summary>
    /// Verifies terminal-equivalent configured collisions leave the complete baseline map active.
    /// </summary>
    [TestMethod]
    public void TryApply_WithTerminalEquivalentCollision_RejectsCompleteOverrideSet()
    {
        var bindings = CreateBindings();
        bindings.Key(Hex1bKey.Tab).Triggers(
            WorkspaceActionIds.CyclePanes,
            static () => { },
            "Cycle panes");
        var configuration = Configuration(
            "gitsail.keymap.repository.refresh",
            "Ctrl+I");

        var applied = WorkspaceKeymap.TryApply(bindings, configuration, out var error);

        Assert.IsFalse(applied);
        StringAssert.Contains(error, "same baseline terminal input");
        Assert.HasCount(2, bindings.GetBindings(WorkspaceActionIds.Refresh));
        Assert.HasCount(1, bindings.GetBindings(WorkspaceActionIds.CyclePanes));
    }

    /// <summary>
    /// Verifies an unknown configured key name cannot partially replace baseline bindings.
    /// </summary>
    [TestMethod]
    public void TryApply_WithUnsupportedChord_RejectsCompleteOverrideSet()
    {
        var bindings = CreateBindings();
        var configuration = Configuration(
            "gitsail.keymap.repository.refresh",
            "Hyper");

        var applied = WorkspaceKeymap.TryApply(bindings, configuration, out var error);

        Assert.IsFalse(applied);
        StringAssert.Contains(error, "unsupported baseline chord");
        Assert.HasCount(2, bindings.GetBindings(WorkspaceActionIds.Refresh));
    }

    /// <summary>
    /// Verifies edited keymap values reject unknown actions and collisions before configuration writes.
    /// </summary>
    [TestMethod]
    public void TryValidateCandidate_WithUnknownOrCollidingAction_RejectsSave()
    {
        var bindings = CreateBindings();

        Assert.IsFalse(WorkspaceKeymap.TryValidateCandidate(
            bindings,
            "gitsail.keymap.missing.action",
            "F12",
            out var unknownError));
        StringAssert.Contains(unknownError, "not registered");
        Assert.IsFalse(WorkspaceKeymap.TryValidateCandidate(
            bindings,
            "gitsail.keymap.repository.refresh",
            "F1",
            out var collisionError));
        StringAssert.Contains(collisionError, "same baseline terminal input");
        Assert.IsTrue(WorkspaceKeymap.TryValidateCandidate(
            bindings,
            "gitsail.keymap.repository.refresh",
            "F12",
            out var validError),
            validError);
    }

    /// <summary>
    /// Verifies every complete baseline workspace context remains byte-level collision-free.
    /// </summary>
    [TestMethod]
    public void TryApply_WithCompleteBaselineContexts_AcceptsEveryActiveBindingMap()
    {
        AssertCollisionFree(CreateGlobalBindings(), "global workspace");
        AssertCollisionFree(CreateChangedPathBindings(), "changed-path list");
        AssertCollisionFree(CreateDiffBindings(), "read-only diff");
        AssertCollisionFree(CreateCommitEditorBindings(), "commit editor");
    }

    private static InputBindingsBuilder CreateBindings()
    {
        var bindings = new InputBindingsBuilder();
        bindings.Key(Hex1bKey.F5).Triggers(
            WorkspaceActionIds.Refresh,
            static () => { },
            "Refresh");
        bindings.Ctrl().Key(Hex1bKey.R).Triggers(
            WorkspaceActionIds.Refresh,
            static () => { },
            "Refresh");
        bindings.Key(Hex1bKey.F1).Triggers(
            WorkspaceActionIds.Help,
            static () => { },
            "Help");
        return bindings;
    }

    private static InputBindingsBuilder CreateGlobalBindings()
    {
        var bindings = new InputBindingsBuilder();
        AddGlobalBindings(bindings);
        bindings.Key(Hex1bKey.A).Triggers(WorkspaceActionIds.StageAll);
        bindings.Shift().Key(Hex1bKey.U).Triggers(WorkspaceActionIds.UnstageAll);
        bindings.Key(Hex1bKey.P).Triggers(WorkspaceActionIds.PrepareUntracked);
        bindings.Key(Hex1bKey.R).Triggers(WorkspaceActionIds.Revert);
        bindings.Shift().Key(Hex1bKey.R).Triggers(WorkspaceActionIds.Revert);
        bindings.Ctrl().Key(Hex1bKey.Z).Triggers(WorkspaceActionIds.UndoRevert);
        return bindings;
    }

    private static InputBindingsBuilder CreateChangedPathBindings()
    {
        var bindings = new InputBindingsBuilder();
        AddGlobalBindings(bindings);
        bindings.Key(Hex1bKey.S).Triggers(WorkspaceActionIds.Stage);
        bindings.Key(Hex1bKey.Spacebar).Triggers(WorkspaceActionIds.Stage);
        bindings.Key(Hex1bKey.A).Triggers(WorkspaceActionIds.StageAll);
        bindings.Shift().Key(Hex1bKey.U).Triggers(WorkspaceActionIds.UnstageAll);
        bindings.Key(Hex1bKey.P).Triggers(WorkspaceActionIds.PrepareUntracked);
        bindings.Key(Hex1bKey.R).Triggers(WorkspaceActionIds.Revert);
        bindings.Shift().Key(Hex1bKey.R).Triggers(WorkspaceActionIds.Revert);
        bindings.Key(Hex1bKey.N).Triggers(WorkspaceActionIds.NextDiffMatch);
        bindings.Shift().Key(Hex1bKey.N).Triggers(WorkspaceActionIds.PreviousDiffMatch);
        return bindings;
    }

    private static InputBindingsBuilder CreateDiffBindings()
    {
        var bindings = new InputBindingsBuilder();
        AddGlobalBindings(bindings);
        bindings.Key(Hex1bKey.S).Triggers(WorkspaceActionIds.StageHunk);
        bindings.Key(Hex1bKey.U).Triggers(WorkspaceActionIds.UnstageHunk);
        bindings.Key(Hex1bKey.A).Triggers(WorkspaceActionIds.StageAll);
        bindings.Shift().Key(Hex1bKey.U).Triggers(WorkspaceActionIds.UnstageAll);
        bindings.Key(Hex1bKey.P).Triggers(WorkspaceActionIds.PrepareUntracked);
        bindings.Key(Hex1bKey.R).Triggers(WorkspaceActionIds.Revert);
        bindings.Shift().Key(Hex1bKey.R).Triggers(WorkspaceActionIds.Revert);
        bindings.Ctrl().Key(Hex1bKey.Z).Triggers(WorkspaceActionIds.UndoRevert);
        bindings.Key(Hex1bKey.J).Triggers(WorkspaceActionIds.NextHunk);
        bindings.Key(Hex1bKey.K).Triggers(WorkspaceActionIds.PreviousHunk);
        bindings.Key(Hex1bKey.N).Triggers(WorkspaceActionIds.NextDiffMatch);
        bindings.Shift().Key(Hex1bKey.N).Triggers(WorkspaceActionIds.PreviousDiffMatch);
        bindings.Key(Hex1bKey.L).Triggers(WorkspaceActionIds.SelectedLines);
        return bindings;
    }

    private static InputBindingsBuilder CreateCommitEditorBindings()
    {
        var bindings = new InputBindingsBuilder();
        RegisterActions(bindings);
        bindings.Key(Hex1bKey.F1).Triggers(WorkspaceActionIds.Help);
        bindings.Key(Hex1bKey.F2).Triggers(WorkspaceActionIds.CommandPalette);
        bindings.Key(Hex1bKey.F3).Triggers(WorkspaceActionIds.NextDiffMatch);
        bindings.Shift().Key(Hex1bKey.F3).Triggers(WorkspaceActionIds.PreviousDiffMatch);
        bindings.Key(Hex1bKey.F4).Triggers(WorkspaceActionIds.Primary);
        bindings.Key(Hex1bKey.F5).Triggers(WorkspaceActionIds.Refresh);
        bindings.Ctrl().Key(Hex1bKey.R).Triggers(WorkspaceActionIds.Refresh);
        bindings.Key(Hex1bKey.F6).Triggers(WorkspaceActionIds.CyclePanes);
        bindings.Key(Hex1bKey.F7).Triggers(WorkspaceActionIds.FindChangedPath);
        bindings.Key(Hex1bKey.F8).Triggers(WorkspaceActionIds.Branches);
        bindings.Key(Hex1bKey.F9).Triggers(WorkspaceActionIds.Stashes);
        bindings.Key(Hex1bKey.F10).Triggers(WorkspaceActionIds.ApplicationMenu);
        bindings.Ctrl().Key(Hex1bKey.W).Triggers(WorkspaceActionIds.CloseWindow);
        bindings.Ctrl().Key(Hex1bKey.Q).Triggers(WorkspaceActionIds.Quit);
        return bindings;
    }

    private static void AddGlobalBindings(InputBindingsBuilder bindings)
    {
        RegisterActions(bindings);
        bindings.Key(Hex1bKey.Oem4).Triggers(WorkspaceActionIds.LessContext);
        bindings.Key(Hex1bKey.Oem6).Triggers(WorkspaceActionIds.MoreContext);
        bindings.Key(Hex1bKey.F1).Triggers(WorkspaceActionIds.Help);
        bindings.Key(Hex1bKey.F2).Triggers(WorkspaceActionIds.CommandPalette);
        bindings.Key(Hex1bKey.F3).Triggers(WorkspaceActionIds.NextDiffMatch);
        bindings.Shift().Key(Hex1bKey.F3).Triggers(WorkspaceActionIds.PreviousDiffMatch);
        bindings.Key(Hex1bKey.F4).Triggers(WorkspaceActionIds.Primary);
        bindings.Key(Hex1bKey.F5).Triggers(WorkspaceActionIds.Refresh);
        bindings.Ctrl().Key(Hex1bKey.R).Triggers(WorkspaceActionIds.Refresh);
        bindings.Key(Hex1bKey.F6).Triggers(WorkspaceActionIds.CyclePanes);
        bindings.Key(Hex1bKey.F7).Triggers(WorkspaceActionIds.FindChangedPath);
        bindings.Ctrl().Key(Hex1bKey.F).Triggers(WorkspaceActionIds.FindDiffText);
        bindings.Key(Hex1bKey.F8).Triggers(WorkspaceActionIds.Branches);
        bindings.Key(Hex1bKey.F9).Triggers(WorkspaceActionIds.Stashes);
        bindings.Key(Hex1bKey.F10).Triggers(WorkspaceActionIds.ApplicationMenu);
        bindings.Ctrl().Key(Hex1bKey.W).Triggers(WorkspaceActionIds.CloseWindow);
        bindings.Ctrl().Key(Hex1bKey.Q).Triggers(WorkspaceActionIds.Quit);
    }

    private static void RegisterActions(InputBindingsBuilder bindings)
    {
        foreach (var actionId in WorkspaceActionIds.All)
        {
            bindings.Key(Hex1bKey.F12).Triggers(
                actionId,
                static () => { },
                "Generated collision test action");
            bindings.Remove(actionId);
        }
    }

    private static void AssertCollisionFree(
        InputBindingsBuilder bindings,
        string context)
    {
        var applied = WorkspaceKeymap.TryApply(
            bindings,
            new GitConfigurationSnapshot([]),
            out var error);
        Assert.IsTrue(applied, $"{context}: {error}");
    }

    private static GitConfigurationSnapshot Configuration(string key, string value)
        => new(
        [
            new GitConfigurationEntry(
                GitConfigurationScope.Global,
                GitConfigurationOrigin.FromBytes("file:test"u8.ToArray()),
                GitConfigurationKey.FromBytes(Encoding.UTF8.GetBytes(key)),
                GitConfigurationValue.FromBytes(Encoding.UTF8.GetBytes(value))),
        ]);

    private static WorkspaceKeyChord Parse(string value)
    {
        Assert.IsTrue(WorkspaceKeyChord.TryParse(value, out var chord));
        return chord;
    }
}
