using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Keeps exact file bytes separate from ordered per-line Git attribution metadata.
/// </summary>
/// <param name="Path">The exact repository-relative path requested by the user.</param>
/// <param name="ResolvedRevision">The immutable commit used for a revision request, or <see langword="null"/> for worktree contents.</param>
/// <param name="Content">The complete exact bytes supplied to or read alongside Git blame.</param>
/// <param name="Attributions">The ordered metadata records for the requested result lines.</param>
/// <param name="EncodingName">The effective encoding name used only for display.</param>
internal sealed record BlameCatalog(
    GitPath Path,
    ObjectId? ResolvedRevision,
    ReadOnlyMemory<byte> Content,
    ImmutableArray<BlameAttribution> Attributions,
    string EncodingName);
