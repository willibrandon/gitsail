using GitSail.Ui;
using Hex1b.Documents;
using Hex1b.Theming;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies unified patch decoration remains complete and theme controlled.
/// </summary>
[TestClass]
public sealed class GitSailDiffDecorationProviderTests
{
    /// <summary>
    /// Verifies every patch line kind receives the expected complete theme-aware span.
    /// </summary>
    [TestMethod]
    public void GetDecorations_WithCompletePatch_UsesThemeElementsThroughFinalColumn()
    {
        const string addedLine = "+added text through the final column";
        var document = new Hex1bDocument(string.Join('\n',
        [
            "diff --git a/file b/file",
            "index 1111111..2222222 100644",
            "--- a/file",
            "+++ b/file",
            "@@ -1 +1 @@ heading",
            addedLine,
            "-removed text",
            "\\ No newline at end of file",
            " context",
        ]));
        var provider = new GitSailDiffDecorationProvider();

        var spans = provider.GetDecorations(1, 9, document);

        Assert.HasCount(8, spans);
        var addition = spans.Single(span => span.Start.Line == 6);
        Assert.AreEqual(1, addition.Start.Column);
        Assert.AreEqual(addedLine.Length + 1, addition.End.Column);
        Assert.AreSame(
            GitSailDiffTheme.AdditionForegroundColor,
            addition.Decoration.ForegroundThemeElement);
        Assert.AreSame(
            GitSailDiffTheme.AdditionBackgroundColor,
            addition.Decoration.BackgroundThemeElement);
        var configuredColor = Hex1bColor.FromRgb(12, 34, 56);
        var theme = new Hex1bTheme("test")
            .Set(GitSailDiffTheme.AdditionForegroundColor, configuredColor);
        Assert.AreEqual(
            configuredColor,
            addition.Decoration.ResolveForeground(theme));
    }

    /// <summary>
    /// Verifies document replacement rebuilds cached spans without retaining old lines.
    /// </summary>
    [TestMethod]
    public void GetDecorations_AfterDocumentReplacement_DropsStaleSpans()
    {
        var document = new Hex1bDocument("+old added line\n-removed line");
        var provider = new GitSailDiffDecorationProvider();
        Assert.HasCount(2, provider.GetDecorations(1, 2, document));

        document.Apply(new ReplaceOperation(
            new DocumentRange(DocumentOffset.Zero, new DocumentOffset(document.Length)),
            " context only"));

        Assert.IsEmpty(provider.GetDecorations(1, 2, document));
    }
}
