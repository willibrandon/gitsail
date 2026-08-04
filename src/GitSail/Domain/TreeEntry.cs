namespace GitSail.Domain;

/// <summary>
/// Describes one exact entry in a Git tree object.
/// </summary>
/// <param name="Kind">The typed tree entry kind.</param>
/// <param name="Mode">The canonical six-digit octal Git mode text.</param>
/// <param name="ObjectId">The exact referenced object identifier.</param>
/// <param name="Size">The exact blob size, or <see langword="null"/> for trees and gitlinks.</param>
/// <param name="Name">The exact entry name emitted by Git.</param>
internal sealed record TreeEntry(
    TreeEntryKind Kind,
    string Mode,
    ObjectId ObjectId,
    long? Size,
    GitPath Name);
