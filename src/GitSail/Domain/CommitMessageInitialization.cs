namespace GitSail.Domain;

/// <summary>
/// Contains the selected initial commit-editor message and its user-visible source.
/// </summary>
/// <param name="Message">The complete UTF-8 editor message.</param>
/// <param name="Kind">The source that won initialization precedence.</param>
internal sealed record CommitMessageInitialization(
    string Message,
    CommitMessageInitializationKind Kind);
