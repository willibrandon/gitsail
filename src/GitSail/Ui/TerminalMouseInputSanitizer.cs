namespace GitSail.Ui;

/// <summary>
/// Removes bare SGR mouse reports that a Windows console can leak as ordinary text.
/// </summary>
internal sealed class TerminalMouseInputSanitizer
{
    private const int MaximumReportLength = 64;
    private readonly List<byte> _candidate = [];

    /// <summary>
    /// Gets whether an incomplete terminal sequence is waiting for another input block.
    /// </summary>
    internal bool HasPendingInput => _candidate.Count > 0;

    /// <summary>
    /// Gets whether the pending bytes already identify an SGR mouse report.
    /// </summary>
    internal bool HasRecognizedMouseReport
        => _candidate.Count >= 2 &&
            ((_candidate[0] == (byte)'[' && _candidate[1] == (byte)'<') ||
             (_candidate.Count >= 3 &&
              _candidate[0] == 0x1B &&
              _candidate[1] == (byte)'[' &&
              _candidate[2] == (byte)'<'));

    /// <summary>
    /// Returns terminal input with complete bare mouse reports removed across read boundaries.
    /// </summary>
    /// <param name="input">The next raw terminal input block.</param>
    /// <returns>The input bytes that are safe to pass to the terminal decoder.</returns>
    internal ReadOnlyMemory<byte> Filter(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var output = new List<byte>(input.Length + _candidate.Count);
        for (var index = 0; index < input.Length; index++)
        {
            var current = input[index];

            if (_candidate.Count > 0)
            {
                _candidate.Add(current);
                var classification = ClassifyCandidate();
                if (classification == CandidateComplete)
                {
                    if (_candidate[0] == 0x1B)
                    {
                        output.AddRange(_candidate);
                    }

                    _candidate.Clear();
                }
                else if (classification == CandidateInvalid)
                {
                    output.AddRange(_candidate);
                    _candidate.Clear();
                }

                continue;
            }

            if (current == 0x1B)
            {
                _candidate.Add(current);
                continue;
            }

            if (current == (byte)'[')
            {
                _candidate.Add(current);
                continue;
            }

            output.Add(current);
        }

        return output.Count == 0 ? ReadOnlyMemory<byte>.Empty : output.ToArray();
    }

    /// <summary>
    /// Returns an incomplete sequence unchanged after its bounded continuation wait expires.
    /// </summary>
    /// <returns>The pending bytes, or an empty block when no sequence is pending.</returns>
    internal ReadOnlyMemory<byte> FlushPendingInput()
    {
        if (_candidate.Count == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var pending = _candidate.ToArray();
        _candidate.Clear();
        return pending;
    }

    /// <summary>
    /// Discards a recognized mouse report that never supplied its terminator.
    /// </summary>
    internal void DiscardPendingMouseReport()
    {
        if (!HasRecognizedMouseReport)
        {
            throw new InvalidOperationException(
                "Only a recognized SGR mouse report may be discarded as terminal input.");
        }

        _candidate.Clear();
    }

    private const int CandidatePartial = 0;
    private const int CandidateComplete = 1;
    private const int CandidateInvalid = 2;

    private int ClassifyCandidate()
    {
        if (_candidate.Count > MaximumReportLength)
        {
            return CandidateInvalid;
        }

        var escapePrefixed = _candidate[0] == 0x1B;
        var prefixLength = escapePrefixed ? 3 : 2;
        if (escapePrefixed)
        {
            if (_candidate.Count == 1)
            {
                return CandidatePartial;
            }

            if (_candidate[1] != (byte)'[')
            {
                return CandidateInvalid;
            }

            if (_candidate.Count == 2)
            {
                return CandidatePartial;
            }

            if (_candidate[2] != (byte)'<')
            {
                return CandidateInvalid;
            }
        }
        else
        {
            if (_candidate.Count == 1)
            {
                return CandidatePartial;
            }

            if (_candidate[1] != (byte)'<')
            {
                return CandidateInvalid;
            }
        }

        var index = prefixLength;
        for (var segment = 0; segment < 3; segment++)
        {
            if (index >= _candidate.Count)
            {
                return CandidatePartial;
            }

            var digitStart = index;
            while (index < _candidate.Count && IsAsciiDigit(_candidate[index]))
            {
                index++;
            }

            if (index == digitStart)
            {
                return CandidateInvalid;
            }

            if (segment < 2)
            {
                if (index >= _candidate.Count)
                {
                    return CandidatePartial;
                }

                if (_candidate[index] != (byte)';')
                {
                    return CandidateInvalid;
                }

                index++;
                continue;
            }

            if (index >= _candidate.Count)
            {
                return CandidatePartial;
            }

            return index == _candidate.Count - 1 &&
                (_candidate[index] == (byte)'M' || _candidate[index] == (byte)'m')
                    ? CandidateComplete
                    : CandidateInvalid;
        }

        return CandidateInvalid;
    }

    private static bool IsAsciiDigit(byte value)
        => value is >= (byte)'0' and <= (byte)'9';
}
