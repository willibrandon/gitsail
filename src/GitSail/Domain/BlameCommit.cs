namespace GitSail.Domain;

/// <summary>
/// Retains the exact commit identity and bounded metadata associated with blamed lines.
/// </summary>
/// <param name="ObjectId">The commit object identifier, including Git's zero worktree identity.</param>
/// <param name="AuthorName">The author name bytes emitted in forced UTF-8.</param>
/// <param name="AuthorEmail">The author email bytes emitted in forced UTF-8.</param>
/// <param name="AuthoredAt">The exact author instant reported by Git.</param>
/// <param name="AuthorTimeZone">The original numeric author time-zone text.</param>
/// <param name="Summary">The commit summary bytes emitted in forced UTF-8.</param>
internal sealed record BlameCommit(
    ObjectId ObjectId,
    ReadOnlyMemory<byte> AuthorName,
    ReadOnlyMemory<byte> AuthorEmail,
    DateTimeOffset AuthoredAt,
    string AuthorTimeZone,
    ReadOnlyMemory<byte> Summary)
{
    /// <summary>
    /// Gets whether Git used its all-zero identity for uncommitted worktree content.
    /// </summary>
    internal bool IsUncommitted => ObjectId.GetBytes().IndexOfAnyExcept((byte)0) < 0;
}
