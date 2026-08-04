namespace GitSail.Domain;

/// <summary>
/// Describes an interactive or stopped rebase currently owned by Git.
/// </summary>
/// <param name="CurrentCommit">The exact commit currently being applied, when Git exposes one.</param>
/// <param name="CanEditTodo">Whether Git's interactive todo is currently available.</param>
internal sealed record RebaseState(ObjectId? CurrentCommit, bool CanEditTodo);
