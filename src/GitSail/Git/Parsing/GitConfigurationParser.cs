using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail.Git.Parsing;

/// <summary>
/// Parses NUL-framed Git configuration scope, origin, key, and value records.
/// </summary>
internal sealed class GitConfigurationParser
{
    private readonly int _maximumRecordBytes;

    /// <summary>
    /// Initializes a configuration parser with a bounded maximum record size.
    /// </summary>
    /// <param name="maximumRecordBytes">The positive maximum byte count for one NUL-delimited field.</param>
    internal GitConfigurationParser(int maximumRecordBytes = 16 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecordBytes);
        _maximumRecordBytes = maximumRecordBytes;
    }

    /// <summary>
    /// Parses complete output from <c>git config --null --list --show-origin --show-scope</c>.
    /// </summary>
    /// <param name="bytes">The complete bounded NUL-framed response.</param>
    /// <returns>The ordered explicit configuration entries.</returns>
    internal ImmutableArray<GitConfigurationEntry> Parse(ReadOnlySpan<byte> bytes)
    {
        var entries = ImmutableArray.CreateBuilder<GitConfigurationEntry>();
        while (!bytes.IsEmpty)
        {
            var scopeBytes = TakeField(ref bytes);
            var originBytes = TakeField(ref bytes);
            var keyValueBytes = TakeField(ref bytes);
            var separator = keyValueBytes.IndexOf((byte)'\n');
            if (separator <= 0)
            {
                throw new InvalidDataException("Git configuration output omitted the key/value separator.");
            }

            try
            {
                entries.Add(new GitConfigurationEntry(
                    ParseScope(scopeBytes),
                    GitConfigurationOrigin.FromBytes(originBytes),
                    GitConfigurationKey.FromBytes(keyValueBytes[..separator]),
                    GitConfigurationValue.FromBytes(keyValueBytes[(separator + 1)..])));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Git configuration output contained an invalid field.", exception);
            }
        }

        return entries.ToImmutable();
    }

    private ReadOnlySpan<byte> TakeField(ref ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException("Git configuration output ended before a NUL field terminator.");
        }

        if (terminator > _maximumRecordBytes)
        {
            throw new InvalidDataException("Git configuration output contained a field above the configured limit.");
        }

        var field = bytes[..terminator];
        bytes = bytes[(terminator + 1)..];
        return field;
    }

    private static GitConfigurationScope ParseScope(ReadOnlySpan<byte> bytes)
    {
        if (bytes.SequenceEqual("worktree"u8))
        {
            return GitConfigurationScope.Worktree;
        }

        if (bytes.SequenceEqual("local"u8))
        {
            return GitConfigurationScope.Local;
        }

        if (bytes.SequenceEqual("global"u8))
        {
            return GitConfigurationScope.Global;
        }

        if (bytes.SequenceEqual("system"u8))
        {
            return GitConfigurationScope.System;
        }

        if (bytes.SequenceEqual("command"u8))
        {
            return GitConfigurationScope.Command;
        }

        if (bytes.SequenceEqual("unknown"u8))
        {
            return GitConfigurationScope.Unknown;
        }

        throw new InvalidDataException("Git configuration output contained an unknown scope.");
    }
}
