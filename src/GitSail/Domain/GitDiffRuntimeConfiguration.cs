using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains the validated effective configuration shared by status, diff capture, and diff presentation.
/// </summary>
internal sealed record GitDiffRuntimeConfiguration
{
    private GitDiffRuntimeConfiguration(
        int contextLines,
        ImmutableArray<string> additionalOptions,
        GitRenameDetectionMode renameDetection,
        int renameLimit,
        int renameThreshold,
        int tabSize)
    {
        if (contextLines is < 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLines));
        }

        if (!GitDiffOptions.TryValidateItems(additionalOptions, out var optionError))
        {
            throw new ArgumentException(optionError, nameof(additionalOptions));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(renameLimit);
        if (renameThreshold is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(renameThreshold));
        }

        if (tabSize is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(tabSize));
        }

        ContextLines = contextLines;
        AdditionalOptions = additionalOptions.IsDefault ? [] : additionalOptions;
        RenameDetection = renameDetection;
        RenameLimit = renameLimit;
        RenameThreshold = renameThreshold;
        TabSize = tabSize;
    }

    /// <summary>
    /// Gets the safe registered defaults used when no explicit valid value is effective.
    /// </summary>
    internal static GitDiffRuntimeConfiguration Default { get; } = new(
        contextLines: 5,
        additionalOptions: [],
        renameDetection: GitRenameDetectionMode.Renames,
        renameLimit: 1000,
        renameThreshold: 50,
        tabSize: 8);

    /// <summary>
    /// Gets the explicit unchanged-line count appended after configured context options.
    /// </summary>
    internal int ContextLines { get; }

    /// <summary>
    /// Gets the complete validated additional diff arguments in configured order.
    /// </summary>
    internal ImmutableArray<string> AdditionalOptions { get; }

    /// <summary>
    /// Gets whether raw status and patches detect neither renames, renames, or renames and copies.
    /// </summary>
    internal GitRenameDetectionMode RenameDetection { get; }

    /// <summary>
    /// Gets the maximum candidate count for exhaustive rename or copy detection, where zero is unlimited.
    /// </summary>
    internal int RenameLimit { get; }

    /// <summary>
    /// Gets the configured rename and copy similarity percentage.
    /// </summary>
    internal int RenameThreshold { get; }

    /// <summary>
    /// Gets the terminal-cell width used to present tab characters in diff editors.
    /// </summary>
    internal int TabSize { get; }

    /// <summary>
    /// Resolves every runtime diff setting from one ordered typed configuration snapshot.
    /// </summary>
    /// <param name="configuration">The complete effective configuration snapshot.</param>
    /// <returns>The validated runtime values with safe defaults for absent or invalid entries.</returns>
    internal static GitDiffRuntimeConfiguration Resolve(GitConfigurationSnapshot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.Resolve("gui.diffopts", GitConfigurationScope.Local)
            .EffectiveParsedValue?.Items ?? [];
        var renameText = configuration.Resolve("diff.renames", GitConfigurationScope.Local)
            .EffectiveParsedValue?.Text;
        var renameDetection = renameText switch
        {
            "false" => GitRenameDetectionMode.Disabled,
            "copies" => GitRenameDetectionMode.Copies,
            _ => GitRenameDetectionMode.Renames,
        };
        return new GitDiffRuntimeConfiguration(
            ResolveInteger(configuration, "gui.diffcontext", Default.ContextLines, 0, 100_000),
            options,
            renameDetection,
            ResolveInteger(configuration, "diff.renamelimit", Default.RenameLimit, 0, int.MaxValue),
            ResolveInteger(configuration, "gitsail.renamethreshold", Default.RenameThreshold, 0, 100),
            ResolveInteger(configuration, "gui.tabsize", Default.TabSize, 1, 99));
    }

    /// <summary>
    /// Creates the same validated configuration with a new interactive context-line count.
    /// </summary>
    /// <param name="contextLines">The nonnegative context-line count selected by the user.</param>
    /// <returns>A new immutable runtime configuration.</returns>
    internal GitDiffRuntimeConfiguration WithContextLines(int contextLines)
        => new(
            contextLines,
            AdditionalOptions,
            RenameDetection,
            RenameLimit,
            RenameThreshold,
            TabSize);

    private static int ResolveInteger(
        GitConfigurationSnapshot configuration,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        var value = configuration.Resolve(key, GitConfigurationScope.Local)
            .EffectiveParsedValue?.IntegerValue;
        return value is not null && value >= minimum && value <= maximum
            ? checked((int)value.Value)
            : fallback;
    }
}
