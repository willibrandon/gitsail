using GitSail.Domain;

namespace GitSail.Git.Execution;

/// <summary>
/// Contains one authenticated single-use sequence-editor request passed through Git.
/// </summary>
/// <param name="FilePath">The exact protected request file path.</param>
/// <param name="FilePathText">The request path representation placed in the child environment.</param>
/// <param name="Secret">The random request authentication secret.</param>
internal sealed record RebaseSequenceEditorRequestHandle(
    GitPath FilePath,
    string FilePathText,
    string Secret);
