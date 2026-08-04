using GitSail.Domain;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace GitSail.Git.Parsing;

/// <summary>
/// Parses bounded branch-ref and linked-worktree machine output without losing native bytes.
/// </summary>
internal static class BranchCatalogParser
{
    private const int MaximumBranchCount = 1_000_000;
    private const int MaximumWorktreeCount = 100_000;
    private const int MaximumFieldBytes = 1024 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Parses <c>git worktree list --porcelain -z</c> output into exact records.
    /// </summary>
    /// <param name="output">The bounded exact standard-output bytes.</param>
    /// <returns>The complete immutable worktree records.</returns>
    internal static ImmutableArray<WorktreeInfo> ParseWorktrees(ReadOnlySpan<byte> output)
    {
        var worktrees = ImmutableArray.CreateBuilder<WorktreeInfo>();
        GitPath? path = null;
        ObjectId? headObjectId = null;
        RefName? branchName = null;
        var isDetached = false;
        var isBare = false;
        var isLocked = false;
        string? lockReason = null;
        var isPrunable = false;
        string? prunableReason = null;
        var offset = 0;
        while (offset < output.Length)
        {
            var terminator = output[offset..].IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidDataException("Git worktree porcelain output has an unterminated field.");
            }

            if (terminator > MaximumFieldBytes)
            {
                throw new InvalidDataException("A Git worktree porcelain field exceeds the supported limit.");
            }

            var field = output.Slice(offset, terminator);
            offset += terminator + 1;
            if (field.IsEmpty)
            {
                if (path is null)
                {
                    throw new InvalidDataException("Git worktree porcelain output contains an empty record.");
                }

                if (isDetached && branchName is not null)
                {
                    throw new InvalidDataException("A Git worktree record cannot be both detached and attached.");
                }

                if (worktrees.Count == MaximumWorktreeCount)
                {
                    throw new InvalidDataException("Git returned more worktrees than the supported limit.");
                }

                worktrees.Add(new WorktreeInfo(
                    path,
                    headObjectId,
                    branchName,
                    isBare,
                    isLocked,
                    lockReason,
                    isPrunable,
                    prunableReason));
                path = null;
                headObjectId = null;
                branchName = null;
                isDetached = false;
                isBare = false;
                isLocked = false;
                lockReason = null;
                isPrunable = false;
                prunableReason = null;
                continue;
            }

            var separator = field.IndexOf((byte)' ');
            var key = separator < 0 ? field : field[..separator];
            var value = separator < 0 ? [] : field[(separator + 1)..];
            if (key.SequenceEqual("worktree"u8))
            {
                if (path is not null || value.IsEmpty)
                {
                    throw new InvalidDataException("A Git worktree record has a missing or duplicate path.");
                }

                path = CreateNativePath(value);
            }
            else if (key.SequenceEqual("HEAD"u8))
            {
                if (headObjectId is not null || !ObjectId.TryParseHex(value, out headObjectId))
                {
                    throw new InvalidDataException("A Git worktree record has a missing, duplicate, or invalid HEAD.");
                }
            }
            else if (key.SequenceEqual("branch"u8))
            {
                if (branchName is not null || value.IsEmpty)
                {
                    throw new InvalidDataException("A Git worktree record has a missing or duplicate branch.");
                }

                branchName = RefName.FromBytes(value);
            }
            else if (key.SequenceEqual("detached"u8))
            {
                RequireFlagWithoutValue(value, "detached");
                isDetached = true;
            }
            else if (key.SequenceEqual("bare"u8))
            {
                RequireFlagWithoutValue(value, "bare");
                isBare = true;
            }
            else if (key.SequenceEqual("locked"u8))
            {
                if (isLocked)
                {
                    throw new InvalidDataException("A Git worktree record contains a duplicate locked field.");
                }

                isLocked = true;
                lockReason = value.IsEmpty ? null : FormatDisplayText(value);
            }
            else if (key.SequenceEqual("prunable"u8))
            {
                if (isPrunable)
                {
                    throw new InvalidDataException("A Git worktree record contains a duplicate prunable field.");
                }

                isPrunable = true;
                prunableReason = value.IsEmpty ? null : FormatDisplayText(value);
            }
        }

        if (path is not null)
        {
            throw new InvalidDataException("Git worktree porcelain output has an unterminated record.");
        }

        return worktrees.ToImmutable();
    }

    /// <summary>
    /// Parses the exact NUL-field branch format used by the branch service.
    /// </summary>
    /// <param name="output">The bounded exact standard-output bytes.</param>
    /// <param name="worktrees">The stable worktree records used to assign branch occupancy.</param>
    /// <returns>The complete immutable local and remote-tracking branch records.</returns>
    internal static ImmutableArray<BranchInfo> ParseBranches(
        ReadOnlySpan<byte> output,
        ImmutableArray<WorktreeInfo> worktrees)
    {
        var branches = ImmutableArray.CreateBuilder<BranchInfo>();
        var offset = 0;
        while (offset < output.Length)
        {
            var lineEnding = output[offset..].IndexOf((byte)'\n');
            if (lineEnding < 0)
            {
                throw new InvalidDataException("Git branch output has an unterminated record.");
            }

            if (lineEnding > MaximumFieldBytes * 6)
            {
                throw new InvalidDataException("A Git branch record exceeds the supported limit.");
            }

            var record = output.Slice(offset, lineEnding);
            offset += lineEnding + 1;
            var fields = SplitBranchFields(record);
            var fullName = RefName.FromBytes(fields[0]);
            var (kind, shortName) = ParseBranchName(fullName);
            if (!ObjectId.TryParseHex(fields[1], out var targetObjectId))
            {
                throw new InvalidDataException("Git returned an invalid branch target object identifier.");
            }

            var upstream = fields[2].Length == 0 ? null : RefName.FromBytes(fields[2]);
            var (ahead, behind, gone) = ParseTracking(fields[3]);
            var isCurrent = fields[4].AsSpan().SequenceEqual("*"u8);
            if (!isCurrent && !fields[4].AsSpan().SequenceEqual(" "u8))
            {
                throw new InvalidDataException("Git returned an invalid current-branch marker.");
            }

            var symbolicTarget = fields[5].Length == 0 ? null : RefName.FromBytes(fields[5]);
            var occupiedWorktrees = worktrees
                .Where(worktree => Equals(worktree.BranchName, fullName))
                .Select(static worktree => worktree.Path)
                .ToImmutableArray();
            if (branches.Any(branch => branch.FullName.Equals(fullName)))
            {
                throw new InvalidDataException("Git returned a duplicate branch ref.");
            }

            if (branches.Count == MaximumBranchCount)
            {
                throw new InvalidDataException("Git returned more branches than the supported limit.");
            }

            branches.Add(new BranchInfo(
                fullName,
                shortName,
                kind,
                targetObjectId!,
                upstream,
                ahead,
                behind,
                gone,
                isCurrent,
                occupiedWorktrees,
                symbolicTarget));
        }

        return branches.ToImmutable();
    }

    private static byte[][] SplitBranchFields(ReadOnlySpan<byte> record)
    {
        var fields = new byte[6][];
        var offset = 0;
        for (var index = 0; index < fields.Length; index++)
        {
            var terminator = record[offset..].IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidDataException("A Git branch record has too few NUL-delimited fields.");
            }

            if (terminator > MaximumFieldBytes)
            {
                throw new InvalidDataException("A Git branch field exceeds the supported limit.");
            }

            fields[index] = record.Slice(offset, terminator).ToArray();
            offset += terminator + 1;
        }

        if (offset != record.Length)
        {
            throw new InvalidDataException("A Git branch record has unexpected trailing fields.");
        }

        if (fields[0].Length == 0 || fields[1].Length == 0)
        {
            throw new InvalidDataException("A Git branch record has an empty ref or target.");
        }

        return fields;
    }

    private static (BranchKind Kind, RefName ShortName) ParseBranchName(RefName fullName)
    {
        ReadOnlySpan<byte> localPrefix = "refs/heads/"u8;
        ReadOnlySpan<byte> remotePrefix = "refs/remotes/"u8;
        var bytes = fullName.GetBytes();
        if (bytes.StartsWith(localPrefix) && bytes.Length > localPrefix.Length)
        {
            return (BranchKind.Local, RefName.FromBytes(bytes[localPrefix.Length..]));
        }

        if (bytes.StartsWith(remotePrefix) && bytes.Length > remotePrefix.Length)
        {
            return (BranchKind.RemoteTracking, RefName.FromBytes(bytes[remotePrefix.Length..]));
        }

        throw new InvalidDataException("Git returned a ref outside the requested branch namespaces.");
    }

    private static (int Ahead, int Behind, bool Gone) ParseTracking(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return (0, 0, false);
        }

        if (value.SequenceEqual("[gone]"u8))
        {
            return (0, 0, true);
        }

        if (value.Length < 3 || value[0] != (byte)'[' || value[^1] != (byte)']')
        {
            throw new InvalidDataException("Git returned an invalid upstream tracking description.");
        }

        var text = Encoding.ASCII.GetString(value[1..^1]);
        var ahead = 0;
        var behind = 0;
        foreach (var component in text.Split(", ", StringSplitOptions.None))
        {
            if (component.StartsWith("ahead ", StringComparison.Ordinal))
            {
                ahead = ParseTrackingCount(component[6..]);
            }
            else if (component.StartsWith("behind ", StringComparison.Ordinal))
            {
                behind = ParseTrackingCount(component[7..]);
            }
            else
            {
                throw new InvalidDataException("Git returned an invalid upstream tracking component.");
            }
        }

        if (ahead == 0 && behind == 0)
        {
            throw new InvalidDataException("Git returned an empty upstream tracking description.");
        }

        return (ahead, behind, false);
    }

    private static int ParseTrackingCount(string text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0
            ? count
            : throw new InvalidDataException("Git returned an invalid upstream tracking count.");

    private static GitPath CreateNativePath(ReadOnlySpan<byte> value)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(
                s_strictUtf8.GetString(value).Replace('/', Path.DirectorySeparatorChar))
            : GitPath.FromUnixBytes(value);

    private static string FormatDisplayText(ReadOnlySpan<byte> value)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(s_strictUtf8.GetString(value)).DisplayText
            : GitPath.FromUnixBytes(value).DisplayText;

    private static void RequireFlagWithoutValue(ReadOnlySpan<byte> value, string fieldName)
    {
        if (!value.IsEmpty)
        {
            throw new InvalidDataException($"Git worktree field '{fieldName}' unexpectedly has a value.");
        }
    }
}
