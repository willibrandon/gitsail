using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Describes one exact commit and its structured history metadata.
/// </summary>
internal sealed class HistoryCommit
{
    private readonly byte[] _authorName;
    private readonly byte[] _authorEmail;
    private readonly byte[] _decorations;
    private readonly byte[] _subject;
    private readonly byte[] _body;

    /// <summary>
    /// Initializes one immutable structured commit record.
    /// </summary>
    /// <param name="objectId">The exact commit object identifier.</param>
    /// <param name="parents">The ordered exact parent commit identifiers.</param>
    /// <param name="authorName">The author name bytes emitted in UTF-8 by Git.</param>
    /// <param name="authorEmail">The author email bytes emitted in UTF-8 by Git.</param>
    /// <param name="authoredAt">The author timestamp including its original offset.</param>
    /// <param name="decorations">The full ref decoration bytes emitted by Git.</param>
    /// <param name="signatureStatus">The machine-readable commit signature status.</param>
    /// <param name="subject">The commit subject bytes emitted in UTF-8 by Git.</param>
    /// <param name="body">The commit body bytes emitted in UTF-8 by Git.</param>
    internal HistoryCommit(
        ObjectId objectId,
        ImmutableArray<ObjectId> parents,
        ReadOnlySpan<byte> authorName,
        ReadOnlySpan<byte> authorEmail,
        DateTimeOffset authoredAt,
        ReadOnlySpan<byte> decorations,
        CommitSignatureStatus signatureStatus,
        ReadOnlySpan<byte> subject,
        ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ValidateText(authorName, nameof(authorName));
        ValidateText(authorEmail, nameof(authorEmail));
        ValidateText(decorations, nameof(decorations));
        ValidateText(subject, nameof(subject));
        ValidateText(body, nameof(body));
        ObjectId = objectId;
        Parents = parents;
        _authorName = authorName.ToArray();
        _authorEmail = authorEmail.ToArray();
        AuthoredAt = authoredAt;
        _decorations = decorations.ToArray();
        SignatureStatus = signatureStatus;
        _subject = subject.ToArray();
        _body = body.ToArray();
    }

    /// <summary>
    /// Gets the exact commit object identifier.
    /// </summary>
    internal ObjectId ObjectId { get; }

    /// <summary>
    /// Gets the ordered exact parent commit identifiers.
    /// </summary>
    internal ImmutableArray<ObjectId> Parents { get; }

    /// <summary>
    /// Gets the author name bytes emitted in UTF-8 by Git.
    /// </summary>
    internal ReadOnlyMemory<byte> AuthorName => _authorName;

    /// <summary>
    /// Gets the author email bytes emitted in UTF-8 by Git.
    /// </summary>
    internal ReadOnlyMemory<byte> AuthorEmail => _authorEmail;

    /// <summary>
    /// Gets the author timestamp including its original offset.
    /// </summary>
    internal DateTimeOffset AuthoredAt { get; }

    /// <summary>
    /// Gets the full ref decoration bytes emitted by Git.
    /// </summary>
    internal ReadOnlyMemory<byte> Decorations => _decorations;

    /// <summary>
    /// Gets Git's machine-readable commit signature status.
    /// </summary>
    internal CommitSignatureStatus SignatureStatus { get; }

    /// <summary>
    /// Gets the commit subject bytes emitted in UTF-8 by Git.
    /// </summary>
    internal ReadOnlyMemory<byte> Subject => _subject;

    /// <summary>
    /// Gets the commit body bytes emitted in UTF-8 by Git.
    /// </summary>
    internal ReadOnlyMemory<byte> Body => _body;

    private static void ValidateText(ReadOnlySpan<byte> value, string parameterName)
    {
        if (value.Contains((byte)0))
        {
            throw new ArgumentException("A structured commit text field cannot contain NUL.", parameterName);
        }
    }
}
