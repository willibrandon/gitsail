using GitSail.Domain;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace GitSail.Git.Parsing;

/// <summary>
/// Parses bounded NUL-framed structured commit records emitted by Git log.
/// </summary>
internal sealed class HistoryLogParser
{
    private const int DefaultMaximumRecordBytes = 16 * 1024 * 1024;
    private const int DefaultMaximumCommitCount = 1_000_000;
    private readonly int _maximumRecordBytes;
    private readonly int _maximumCommitCount;

    /// <summary>
    /// Initializes a bounded structured-history parser.
    /// </summary>
    /// <param name="maximumRecordBytes">The maximum aggregate byte count for one commit record.</param>
    /// <param name="maximumCommitCount">The maximum accepted commit count.</param>
    internal HistoryLogParser(
        int maximumRecordBytes = DefaultMaximumRecordBytes,
        int maximumCommitCount = DefaultMaximumCommitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecordBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCommitCount);
        _maximumRecordBytes = maximumRecordBytes;
        _maximumCommitCount = maximumCommitCount;
    }

    /// <summary>
    /// Parses complete nine-field commit records with an additional NUL record terminator.
    /// </summary>
    /// <param name="bytes">The complete structured Git log byte stream.</param>
    /// <returns>The ordered immutable commit records.</returns>
    internal ImmutableArray<HistoryCommit> Parse(ReadOnlySpan<byte> bytes)
    {
        var commits = ImmutableArray.CreateBuilder<HistoryCommit>();
        while (!bytes.IsEmpty)
        {
            if (commits.Count >= _maximumCommitCount)
            {
                throw new InvalidDataException("Git returned more history records than the configured limit.");
            }

            var recordStartLength = bytes.Length;
            var objectField = TakeField(ref bytes);
            var parentField = TakeField(ref bytes);
            var authorNameField = TakeField(ref bytes);
            var authorEmailField = TakeField(ref bytes);
            var authoredAtField = TakeField(ref bytes);
            var decorationsField = TakeField(ref bytes);
            var signatureField = TakeField(ref bytes);
            var subjectField = TakeField(ref bytes);
            var bodyField = TakeField(ref bytes);
            if (bytes.IsEmpty || bytes[0] != 0)
            {
                throw new InvalidDataException("Git history output ended before its NUL record terminator.");
            }

            bytes = bytes[1..];
            if (recordStartLength - bytes.Length > _maximumRecordBytes)
            {
                throw new InvalidDataException("Git returned a history record above the configured limit.");
            }

            if (!ObjectId.TryParseHex(objectField, out var objectId))
            {
                throw new InvalidDataException("Git returned an invalid history object identifier.");
            }

            var parents = ParseParents(parentField, objectId!.Format);
            if (!DateTimeOffset.TryParse(
                    Encoding.UTF8.GetString(authoredAtField),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var authoredAt))
            {
                throw new InvalidDataException("Git returned an invalid history author timestamp.");
            }

            commits.Add(new HistoryCommit(
                objectId,
                parents,
                authorNameField,
                authorEmailField,
                authoredAt,
                decorationsField,
                ParseSignatureStatus(signatureField),
                subjectField,
                bodyField));
        }

        return commits.ToImmutable();
    }

    private static ImmutableArray<ObjectId> ParseParents(
        ReadOnlySpan<byte> field,
        RepositoryObjectFormat expectedFormat)
    {
        if (field.IsEmpty)
        {
            return [];
        }

        var parents = ImmutableArray.CreateBuilder<ObjectId>();
        while (!field.IsEmpty)
        {
            var separator = field.IndexOf((byte)' ');
            var parentField = separator < 0 ? field : field[..separator];
            if (!ObjectId.TryParseHex(parentField, out var parent) || parent!.Format != expectedFormat)
            {
                throw new InvalidDataException("Git returned an invalid history parent object identifier.");
            }

            parents.Add(parent);
            if (separator < 0)
            {
                break;
            }

            field = field[(separator + 1)..];
            if (field.IsEmpty || field[0] == (byte)' ')
            {
                throw new InvalidDataException("Git returned an invalid history parent list.");
            }
        }

        return parents.ToImmutable();
    }

    private static CommitSignatureStatus ParseSignatureStatus(ReadOnlySpan<byte> field)
        => field.Length == 1
            ? field[0] switch
            {
                (byte)'N' => CommitSignatureStatus.None,
                (byte)'G' => CommitSignatureStatus.Good,
                (byte)'B' => CommitSignatureStatus.Bad,
                (byte)'U' => CommitSignatureStatus.UnknownValidity,
                (byte)'X' => CommitSignatureStatus.ExpiredSignature,
                (byte)'Y' => CommitSignatureStatus.ExpiredKey,
                (byte)'R' => CommitSignatureStatus.RevokedKey,
                (byte)'E' => CommitSignatureStatus.CannotCheck,
                _ => throw new InvalidDataException("Git returned an unknown commit signature status."),
            }
            : throw new InvalidDataException("Git returned an invalid commit signature status.");

    private static ReadOnlySpan<byte> TakeField(ref ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException("Git history output ended before a NUL field terminator.");
        }

        var field = bytes[..terminator];
        bytes = bytes[(terminator + 1)..];
        return field;
    }
}
