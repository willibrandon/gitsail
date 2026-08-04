using GitSail.Domain;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Builds exact NUL-delimited update-index records for resolving or restoring one conflict path.
/// </summary>
internal static class ConflictIndexInfoBuilder
{
    private const int MaximumInputBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Builds removal plus stage-zero records for one resolved blob and canonical regular mode.
    /// </summary>
    /// <param name="path">The exact repository-relative conflict path.</param>
    /// <param name="mode">The selected regular or executable result mode.</param>
    /// <param name="objectId">The exact resolved blob object ID.</param>
    /// <returns>The exact NUL-delimited index-info input.</returns>
    internal static byte[] BuildResolved(
        GitPath path,
        GitFileMode mode,
        ObjectId objectId)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(objectId);
        if (mode is not (GitFileMode.RegularFile or GitFileMode.ExecutableFile))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        var output = new ArrayBufferWriter<byte>();
        AppendRemoval(output, path, objectId.Format);
        AppendStage(output, path, mode, objectId, stageNumber: 0);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Builds removal plus exact stage 1/2/3 records restoring one original unmerged index entry.
    /// </summary>
    /// <param name="path">The exact repository-relative conflict path.</param>
    /// <param name="stages">The original exact optional base, ours, and theirs stages.</param>
    /// <param name="objectFormat">The repository object format used for the removal record.</param>
    /// <returns>The exact NUL-delimited index-info rollback input.</returns>
    internal static byte[] BuildUnmerged(
        GitPath path,
        ConflictStages stages,
        RepositoryObjectFormat objectFormat)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(stages);
        var output = new ArrayBufferWriter<byte>();
        AppendRemoval(output, path, objectFormat);
        AppendOptionalStage(output, path, stages.Base, stageNumber: 1);
        AppendOptionalStage(output, path, stages.Ours, stageNumber: 2);
        AppendOptionalStage(output, path, stages.Theirs, stageNumber: 3);
        return output.WrittenSpan.ToArray();
    }

    private static void AppendOptionalStage(
        ArrayBufferWriter<byte> output,
        GitPath path,
        ConflictStage? stage,
        int stageNumber)
    {
        if (stage is not null)
        {
            AppendStage(output, path, stage.Mode, stage.ObjectId, stageNumber);
        }
    }

    private static void AppendRemoval(
        ArrayBufferWriter<byte> output,
        GitPath path,
        RepositoryObjectFormat objectFormat)
    {
        AppendAscii(output, "0 ");
        AppendAscii(
            output,
            new string('0', objectFormat == RepositoryObjectFormat.Sha1 ? 40 : 64));
        AppendAscii(output, "\t");
        AppendPath(output, path);
    }

    private static void AppendStage(
        ArrayBufferWriter<byte> output,
        GitPath path,
        GitFileMode mode,
        ObjectId objectId,
        int stageNumber)
    {
        AppendAscii(output, Convert.ToString((int)mode, 8));
        AppendAscii(output, " ");
        AppendAscii(output, objectId.ToString());
        AppendAscii(output, " ");
        AppendAscii(output, stageNumber.ToString(CultureInfo.InvariantCulture));
        AppendAscii(output, "\t");
        AppendPath(output, path);
    }

    private static void AppendPath(ArrayBufferWriter<byte> output, GitPath path)
    {
        var bytes = OperatingSystem.IsWindows()
            ? path.Kind == NativePathKind.WindowsUtf16
                ? Encoding.UTF8.GetBytes(path.GetWindowsPath())
                : throw new ArgumentException("A Windows index record requires a Windows path.", nameof(path))
            : path.Kind == NativePathKind.UnixBytes
                ? path.GetUnixBytes().ToArray()
                : throw new ArgumentException("A Unix index record requires a Unix path.", nameof(path));
        AppendBytes(output, bytes);
        AppendBytes(output, [0]);
    }

    private static void AppendAscii(ArrayBufferWriter<byte> output, string value)
        => AppendBytes(output, Encoding.ASCII.GetBytes(value));

    private static void AppendBytes(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        if (output.WrittenCount > MaximumInputBytes - value.Length)
        {
            throw new InvalidDataException("Conflict index input exceeds the configured limit.");
        }

        output.Write(value);
    }
}
