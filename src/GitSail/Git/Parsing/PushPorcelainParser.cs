using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail.Git.Parsing;

/// <summary>
/// Parses bounded Git push porcelain output into exact fully qualified ref mappings.
/// </summary>
internal static class PushPorcelainParser
{
    private const int MaximumLineBytes = 1024 * 1024;
    private const int MaximumUpdates = 100_000;

    /// <summary>
    /// Parses one complete bounded dry-run response without interpreting presentation summaries.
    /// </summary>
    /// <param name="output">The exact standard-output bytes emitted by Git push porcelain mode.</param>
    /// <returns>The exact deduplicated mappings and automatic-upstream intent.</returns>
    internal static PushPorcelainResult Parse(ReadOnlySpan<byte> output)
    {
        var refSpecs = ImmutableArray.CreateBuilder<PushRefSpec>();
        var wouldSetUpstream = false;
        while (!output.IsEmpty)
        {
            var terminator = output.IndexOf((byte)'\n');
            if (terminator < 0)
            {
                throw new InvalidDataException("Git push porcelain output ended before a line terminator.");
            }

            if (terminator > MaximumLineBytes)
            {
                throw new InvalidDataException("Git push porcelain output contains an overlong line.");
            }

            var line = output[..terminator];
            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.StartsWith("Would set upstream of "u8))
            {
                wouldSetUpstream = true;
            }
            else if (line.Length >= 3 && line[1] == (byte)'\t')
            {
                ParseUpdate(line, refSpecs);
            }

            output = output[(terminator + 1)..];
        }

        return new PushPorcelainResult(refSpecs.ToImmutable(), wouldSetUpstream);
    }

    private static void ParseUpdate(
        ReadOnlySpan<byte> line,
        ImmutableArray<PushRefSpec>.Builder refSpecs)
    {
        if (line[0] is not ((byte)' ' or (byte)'+' or (byte)'-' or (byte)'*' or (byte)'!' or (byte)'='))
        {
            throw new InvalidDataException("Git push porcelain output contains an unknown update flag.");
        }

        var mappingAndSummary = line[2..];
        var secondTab = mappingAndSummary.IndexOf((byte)'\t');
        if (secondTab <= 0)
        {
            throw new InvalidDataException("Git push porcelain output contains an incomplete update record.");
        }

        var mapping = mappingAndSummary[..secondTab];
        var separator = mapping.IndexOf((byte)':');
        if (separator < 0 || separator == mapping.Length - 1)
        {
            throw new InvalidDataException("Git push porcelain output contains an invalid ref mapping.");
        }

        var sourceBytes = mapping[..separator];
        var destinationBytes = mapping[(separator + 1)..];
        if ((!sourceBytes.IsEmpty && !sourceBytes.StartsWith("refs/"u8)) ||
            !destinationBytes.StartsWith("refs/"u8))
        {
            throw new InvalidDataException(
                "Git push porcelain output did not provide fully qualified unambiguous refs.");
        }

        var refSpec = new PushRefSpec(
            sourceBytes.IsEmpty ? null : RefName.FromBytes(sourceBytes),
            RefName.FromBytes(destinationBytes));
        if (refSpecs.Any(existing => existing.Equals(refSpec)))
        {
            return;
        }

        if (refSpecs.Count == MaximumUpdates)
        {
            throw new InvalidDataException("Git push porcelain output exceeded the supported update count.");
        }

        refSpecs.Add(refSpec);
    }
}
