using System.Text;

namespace GitSail.Git.Parsing;

/// <summary>
/// Contains one collision-checked marker width and unique labels shared with Git merge output.
/// </summary>
internal sealed class ConflictMarkerSet
{
    private readonly byte[] _openingMarker;
    private readonly byte[] _baseMarker;
    private readonly byte[] _separatorMarker;
    private readonly byte[] _closingMarker;

    /// <summary>
    /// Initializes exact ASCII marker lines from one positive width and unique label token.
    /// </summary>
    /// <param name="markerSize">The number of repeated marker punctuation bytes.</param>
    /// <param name="token">The nonempty lowercase hexadecimal uniqueness token.</param>
    internal ConflictMarkerSet(int markerSize, string token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(markerSize, 7);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A conflict marker token must be lowercase hexadecimal.", nameof(token));
        }

        MarkerSize = markerSize;
        OursLabel = $"gitsail-ours-{token}";
        BaseLabel = $"gitsail-base-{token}";
        TheirsLabel = $"gitsail-theirs-{token}";
        _openingMarker = CreateLabeledMarker('<', markerSize, OursLabel);
        _baseMarker = CreateLabeledMarker('|', markerSize, BaseLabel);
        _separatorMarker = Encoding.ASCII.GetBytes(new string('=', markerSize));
        _closingMarker = CreateLabeledMarker('>', markerSize, TheirsLabel);
    }

    /// <summary>
    /// Gets the repeated marker punctuation width sent to Git.
    /// </summary>
    internal int MarkerSize { get; }

    /// <summary>
    /// Gets the unique current-side label sent to Git.
    /// </summary>
    internal string OursLabel { get; }

    /// <summary>
    /// Gets the unique merge-base label sent to Git.
    /// </summary>
    internal string BaseLabel { get; }

    /// <summary>
    /// Gets the unique incoming-side label sent to Git.
    /// </summary>
    internal string TheirsLabel { get; }

    /// <summary>
    /// Gets the exact opening marker bytes without a line ending.
    /// </summary>
    internal ReadOnlySpan<byte> OpeningMarker => _openingMarker;

    /// <summary>
    /// Gets the exact base marker bytes without a line ending.
    /// </summary>
    internal ReadOnlySpan<byte> BaseMarker => _baseMarker;

    /// <summary>
    /// Gets the exact side separator bytes without a line ending.
    /// </summary>
    internal ReadOnlySpan<byte> SeparatorMarker => _separatorMarker;

    /// <summary>
    /// Gets the exact closing marker bytes without a line ending.
    /// </summary>
    internal ReadOnlySpan<byte> ClosingMarker => _closingMarker;

    private static byte[] CreateLabeledMarker(char punctuation, int markerSize, string label)
        => Encoding.ASCII.GetBytes($"{new string(punctuation, markerSize)} {label}");
}
