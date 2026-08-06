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
