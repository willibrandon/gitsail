using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains one exact directory listing within an immutable commit tree.
/// </summary>
/// <param name="CommitObjectId">The exact commit whose tree is being browsed.</param>
/// <param name="TreeObjectId">The exact tree object listed by Git.</param>
/// <param name="Directory">The exact repository-relative directory, or <see langword="null"/> for the root.</param>
/// <param name="Entries">The ordered immediate tree entries.</param>
internal sealed record TreeCatalog(
    ObjectId CommitObjectId,
    ObjectId TreeObjectId,
    GitPath? Directory,
    ImmutableArray<TreeEntry> Entries);
