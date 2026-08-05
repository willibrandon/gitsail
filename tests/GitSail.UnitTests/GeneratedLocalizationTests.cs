using GitSail.Localization.Generated;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies generated application messages retain typed arguments and English fallback behavior.
/// </summary>
[TestClass]
public sealed class GeneratedLocalizationTests
{
    /// <summary>
    /// Verifies the generated changed-file message selects singular and plural English variants.
    /// </summary>
    [TestMethod]
    public void DiffActivityLoadedChangedFilesForLocale_WithEnglishCounts_SelectsPluralVariant()
    {
        Assert.AreEqual(
            "Loaded 1 changed file",
            AppMessages.DiffActivityLoadedChangedFilesForLocale("en", 1));
        Assert.AreEqual(
            "Loaded 2 changed files",
            AppMessages.DiffActivityLoadedChangedFilesForLocale("en", 2));
    }

    /// <summary>
    /// Verifies an unsupported locale falls back to the English source message.
    /// </summary>
    [TestMethod]
    public void WorkspaceStatusCleanForLocale_WithUnsupportedLocale_ReturnsEnglish()
        => Assert.AreEqual("Working tree clean", AppMessages.WorkspaceStatusCleanForLocale("x-test"));
}
