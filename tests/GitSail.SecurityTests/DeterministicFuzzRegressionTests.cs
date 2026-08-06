using System.Text;
using GitSail.Domain;
using GitSail.Git.Parsing;
using GitSail.Ui;

namespace GitSail.SecurityTests;

/// <summary>
/// Runs deterministic mutation fuzzing over untrusted byte-oriented product boundaries.
/// Makes every discovered crash or nondeterministic result a repeatable CI regression.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DeterministicFuzzRegressionTests
{
    private const int CaseCount = 512;
    private const int MaximumCaseBytes = 2048;
    private const long MaximumAllocatedBytesPerTarget = 16L * 1024 * 1024;
    private static readonly byte[][] s_seeds =
    [
        [],
        [0],
        [10],
        "? path.txt\0"u8.ToArray(),
        "global\0file:test\0user.name\nvalue\0"u8.ToArray(),
        "100644 blob 1111111111111111111111111111111111111111       1\tfile\0"u8.ToArray(),
        "1111111111111111111111111111111111111111 1 1 1\nauthor Test\nfilename file\n"u8.ToArray(),
        "diff --git a/file b/file\n@@ -1 +1 @@\n-old\n+new\n"u8.ToArray(),
        "  refs/heads/main:refs/heads/main\tup to date\n"u8.ToArray(),
        "worktree /repository\0HEAD 1111111111111111111111111111111111111111\0detached\0\0"u8.ToArray(),
        [.. Enumerable.Range(0, 256).Select(static value => (byte)value)],
    ];

    /// <summary>
    /// Verifies every structured Git parser is bounded, deterministic, and fail-closed.
    /// Exercises valid seeds, framing mutations, arbitrary bytes, and maximum-size cases.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public void StructuredParsers_WithDeterministicMutationCorpus_RemainBoundedAndRepeatable()
    {
        var repository = CreateRepository();
        foreach (var input in CreateCorpus())
        {
            AssertDeterministic(
                "status",
                input,
                bytes =>
                {
                    var result = new PorcelainV2StatusParser(MaximumCaseBytes).Parse(
                        bytes,
                        repository,
                        new OperationGeneration(1));
                    return $"{result.Entries.Length}:{result.HeadName?.DisplayText}";
                });
            AssertDeterministic(
                "configuration",
                input,
                bytes =>
                {
                    var result = new GitConfigurationParser(MaximumCaseBytes).Parse(bytes);
                    return string.Join('|', result.Select(static entry =>
                        $"{entry.Scope}:{entry.Key.DisplayText}:" +
                        Convert.ToHexString(entry.Value.GetBytes())));
                });
            AssertDeterministic(
                "tree",
                input,
                bytes =>
                {
                    var result = new TreeEntryParser(MaximumCaseBytes, 256).Parse(bytes);
                    return string.Join('|', result.Select(static entry =>
                        $"{entry.Mode}:{entry.ObjectId}:{entry.Name.DisplayText}"));
                });
            AssertDeterministic(
                "blame",
                input,
                bytes =>
                {
                    var result = new BlameIncrementalParser(256, MaximumCaseBytes).Parse(bytes);
                    return string.Join('|', result.Select(static entry =>
                        $"{entry.Commit.ObjectId}:{entry.ResultLineNumber}:{entry.SourcePath.DisplayText}"));
                });
            AssertDeterministic(
                "history",
                input,
                bytes =>
                {
                    var result = new HistoryLogParser(MaximumCaseBytes, 256).Parse(bytes);
                    return string.Join('|', result.Select(static entry =>
                        $"{entry.ObjectId}:{entry.Parents.Length}:{Convert.ToHexString(entry.Subject.Span)}"));
                });
            AssertDeterministic(
                "stash",
                input,
                bytes =>
                {
                    var result = new StashCatalogParser(MaximumCaseBytes, 256).Parse(bytes);
                    return string.Join('|', result.Select(static entry =>
                        $"{entry.ObjectId}:{entry.Selector}:{entry.CreatedAt.ToUnixTimeSeconds()}"));
                });
            AssertDeterministic(
                "push porcelain",
                input,
                bytes =>
                {
                    var result = PushPorcelainParser.Parse(bytes);
                    return $"{result.WouldSetUpstream}:" +
                        string.Join('|', result.RefSpecs.Select(static refSpec => refSpec.ToString()));
                });
            AssertDeterministic(
                "worktree refs",
                input,
                bytes =>
                {
                    var result = BranchCatalogParser.ParseWorktrees(bytes);
                    return string.Join('|', result.Select(static worktree =>
                        $"{worktree.Path.DisplayText}:{worktree.BranchName?.DisplayText}:" +
                        worktree.HeadObjectId));
                });
            AssertDeterministic(
                "raw patch",
                input,
                bytes =>
                {
                    var result = RawPatchParser.Parse(bytes);
                    return $"{result.HeaderLength}:{result.Hunks.Length}:" +
                        string.Join(',', result.Hunks.Select(static hunk => hunk.Lines.Length));
                });
            AssertDeterministic(
                "quoted diff path",
                input,
                bytes =>
                {
                    var result = GitQuotedPathParser.ParseDiffHeader(bytes);
                    return $"{result.OldPath.DisplayText}:{result.NewPath.DisplayText}";
                });
        }
    }

    /// <summary>
    /// Verifies terminal and URL sanitizers are deterministic across arbitrary byte content.
    /// Ensures controls, malformed UTF-8, credentials, and fragmented mouse input stay bounded.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public void Sanitizers_WithDeterministicMutationCorpus_RemainBoundedAndRepeatable()
    {
        foreach (var input in CreateCorpus())
        {
            AssertDeterministic(
                "terminal text",
                input,
                bytes => TerminalTextSanitizer.Sanitize(Encoding.UTF8.GetString(bytes)));
            AssertDeterministic(
                "remote URL",
                input,
                bytes => RemoteUrl.FromBytes(bytes).RedactedDisplayText);
            AssertDeterministic(
                "mouse input",
                input,
                bytes =>
                {
                    var sanitizer = new TerminalMouseInputSanitizer();
                    var firstLength = bytes.Length / 2;
                    var first = sanitizer.Filter(bytes[..firstLength]).ToArray();
                    var second = sanitizer.Filter(bytes[firstLength..]).ToArray();
                    var pending = sanitizer.HasPendingInput
                        ? sanitizer.FlushPendingInput().ToArray()
                        : [];
                    return Convert.ToHexString([.. first, .. second, .. pending]);
                });
        }
    }

    private static void AssertDeterministic(
        string target,
        byte[] input,
        Func<byte[], string> action)
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var first = CaptureOutcome(action, input);
        var second = CaptureOutcome(action, input);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.AreEqual(first, second, $"{target} produced a nondeterministic result.");
        Assert.IsLessThanOrEqualTo(
            MaximumAllocatedBytesPerTarget,
            allocated,
            $"{target} allocated {allocated:N0} bytes for a {input.Length:N0}-byte fuzz input.");
    }

    private static string CaptureOutcome(
        Func<byte[], string> action,
        byte[] input)
    {
        try
        {
            return "accepted:" + action(input);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException)
        {
            return $"rejected:{exception.GetType().FullName}:{exception.Message}";
        }
    }

    private static IEnumerable<byte[]> CreateCorpus()
    {
        foreach (var seed in s_seeds)
        {
            yield return seed;
        }

        uint state = 0x7A6D_4B21;
        for (var caseIndex = 0; caseIndex < CaseCount; caseIndex++)
        {
            var seed = s_seeds[caseIndex % s_seeds.Length];
            var length = caseIndex % 8 == 0
                ? MaximumCaseBytes
                : (int)(Next(ref state) % 513);
            var bytes = new byte[length];
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = index < seed.Length && caseIndex % 3 != 0
                    ? seed[index]
                    : (byte)Next(ref state);
            }

            var mutationCount = Math.Min(bytes.Length, 1 + (caseIndex % 16));
            for (var mutation = 0; mutation < mutationCount; mutation++)
            {
                var index = (int)(Next(ref state) % (uint)Math.Max(1, bytes.Length));
                if (bytes.Length > 0)
                {
                    bytes[index] ^= (byte)(1u << (int)(Next(ref state) & 7));
                }
            }

            yield return bytes;
        }
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static RepositoryLocation CreateRepository()
    {
        var root = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath("C:\\fuzz-repository")
            : GitPath.FromUnixBytes("/fuzz-repository"u8);
        return new RepositoryLocation(
            root,
            root,
            root,
            Prefix: null,
            RepositoryObjectFormat.Sha1,
            IsBare: false);
    }
}
