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

    /// <summary>
    /// Verifies every required non-English locale is compiled into the generated table.
    /// </summary>
    /// <param name="locale">The normalized required locale.</param>
    /// <param name="expected">The expected localized clean-worktree status.</param>
    [TestMethod]
    [DataRow("bg", "Работното дърво е чисто")]
    [DataRow("de", "Arbeitsverzeichnis unverändert")]
    [DataRow("el", "Το δέντρο εργασίας είναι καθαρό")]
    [DataRow("fr", "Arbre de travail propre")]
    [DataRow("hu", "A munkafa tiszta")]
    [DataRow("it", "Albero di lavoro pulito")]
    [DataRow("ja", "作業ツリーに変更はありません")]
    [DataRow("nb", "Arbeidstreet er rent")]
    [DataRow("pt-BR", "Árvore de trabalho limpa")]
    [DataRow("pt-PT", "Árvore de trabalho limpa")]
    [DataRow("ru", "Рабочее дерево чисто")]
    [DataRow("sv", "Arbetskatalogen är ren")]
    [DataRow("vi", "Cây làm việc sạch")]
    [DataRow("zh-CN", "工作区没有更改")]
    public void WorkspaceStatusCleanForLocale_WithRequiredLocale_ReturnsTranslation(
        string locale,
        string expected)
        => Assert.AreEqual(expected, AppMessages.WorkspaceStatusCleanForLocale(locale));

    /// <summary>
    /// Verifies every required locale selects and formats its ordinary plural form.
    /// </summary>
    /// <param name="locale">The normalized required locale.</param>
    /// <param name="expected">The expected localized two-file status.</param>
    [TestMethod]
    [DataRow("bg", "Заредени са 2 променени файла")]
    [DataRow("de", "2 geänderte Dateien geladen")]
    [DataRow("el", "Φορτώθηκαν 2 τροποποιημένα αρχεία")]
    [DataRow("fr", "2 fichiers modifiés chargés")]
    [DataRow("hu", "2 módosított fájl betöltve")]
    [DataRow("it", "Caricati 2 file modificati")]
    [DataRow("ja", "変更されたファイルを 2 件読み込みました")]
    [DataRow("nb", "Lastet inn 2 endrede filer")]
    [DataRow("pt-BR", "2 arquivos alterados carregados")]
    [DataRow("pt-PT", "Foram carregados 2 ficheiros alterados")]
    [DataRow("ru", "Загружено 2 изменённых файла")]
    [DataRow("sv", "2 ändrade filer lästes in")]
    [DataRow("vi", "Đã tải 2 tệp đã thay đổi")]
    [DataRow("zh-CN", "已加载 2 个已更改的文件")]
    public void DiffActivityLoadedChangedFilesForLocale_WithRequiredLocale_FormatsTranslation(
        string locale,
        string expected)
        => Assert.AreEqual(expected, AppMessages.DiffActivityLoadedChangedFilesForLocale(locale, 2));

    /// <summary>
    /// Verifies the required multi-category locales select their less common forms.
    /// </summary>
    /// <param name="locale">The normalized required locale.</param>
    /// <param name="count">The count used to choose the plural category.</param>
    /// <param name="expected">The expected localized status.</param>
    [TestMethod]
    [DataRow("fr", 0, "0 fichier modifié chargé")]
    [DataRow("fr", 1_000_000, "1000000 de fichiers modifiés chargés")]
    [DataRow("pt-BR", 0, "0 arquivo alterado carregado")]
    [DataRow("pt-BR", 1_000_000, "1000000 de arquivos alterados carregados")]
    [DataRow("pt-PT", 1, "Foi carregado 1 ficheiro alterado")]
    [DataRow("pt-PT", 1_000_000, "Foram carregados 1000000 de ficheiros alterados")]
    [DataRow("ru", 1, "Загружен 1 изменённый файл")]
    [DataRow("ru", 5, "Загружено 5 изменённых файлов")]
    public void DiffActivityLoadedChangedFilesForLocale_WithSpecialCategory_SelectsTranslation(
        string locale,
        int count,
        string expected)
        => Assert.AreEqual(expected, AppMessages.DiffActivityLoadedChangedFilesForLocale(locale, count));

    /// <summary>
    /// Verifies the expansion pseudo-locale is generated for every English message.
    /// </summary>
    [TestMethod]
    public void WorkspaceStatusCleanForLocale_WithExpansionPseudoLocale_ExpandsMessage()
    {
        var message = AppMessages.WorkspaceStatusCleanForLocale("en-XA");

        Assert.StartsWith("⟦", message);
        Assert.EndsWith("~~⟧", message);
        Assert.IsGreaterThan("Working tree clean".Length, message.Length);
    }

    /// <summary>
    /// Verifies generated workspace presentation members return their required Japanese translations.
    /// </summary>
    [TestMethod]
    public void WorkspacePresentationForLocale_WithJapaneseLocale_ReturnsTranslations()
    {
        Assert.AreEqual(
            "コミットメッセージ",
            AppMessages.WorkspaceSectionCommitMessageForLocale("ja"));
        Assert.AreEqual(
            "変更されたパスを選択してパッチを確認してください。",
            AppMessages.WorkspacePromptSelectChangedPathForLocale("ja"));
        Assert.AreEqual("ブランチ", AppMessages.WorkspaceActionBranchesForLocale("ja"));
    }

    /// <summary>
    /// Verifies expansion generation applies to newly localized workspace presentation messages.
    /// </summary>
    [TestMethod]
    public void WorkspacePresentationForLocale_WithExpansionPseudoLocale_ExpandsMessage()
    {
        var message = AppMessages.WorkspacePromptSelectChangedPathForLocale("en-XA");

        Assert.StartsWith("⟦", message);
        Assert.EndsWith("~~⟧", message);
        Assert.IsGreaterThan("Select a changed path to inspect its patch.".Length, message.Length);
    }

    /// <summary>
    /// Verifies the RTL pseudo-locale isolates both the message and its typed argument.
    /// </summary>
    [TestMethod]
    public void DiffActivityLoadedChangedFilesForLocale_WithRtlPseudoLocale_IsolatesMessageAndArgument()
    {
        var message = AppMessages.DiffActivityLoadedChangedFilesForLocale("ar-XB", 2);

        Assert.StartsWith("\u2067⟦", message);
        Assert.EndsWith("⟧\u2069", message);
        Assert.Contains("\u20682\u2069", message);
    }
}
