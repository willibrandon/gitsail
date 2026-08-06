using System.Globalization;
using Hex1b.Input;

namespace GitSail.Ui;

/// <summary>
/// Represents one baseline-terminal key chord used by a workspace action.
/// </summary>
/// <param name="Key">The platform-independent key.</param>
/// <param name="Modifiers">The required key modifiers.</param>
internal readonly record struct WorkspaceKeyChord(Hex1bKey Key, Hex1bModifiers Modifiers)
{
    /// <summary>
    /// Parses one configured chord using GitSail's stable key names.
    /// </summary>
    /// <param name="text">The configured chord text.</param>
    /// <param name="chord">Receives the parsed chord when successful.</param>
    /// <returns><see langword="true"/> when the chord names a supported baseline key.</returns>
    internal static bool TryParse(string text, out WorkspaceKeyChord chord)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = Hex1bModifiers.None;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var modifier = parts[index] switch
            {
                "Ctrl" => Hex1bModifiers.Control,
                "Alt" => Hex1bModifiers.Alt,
                "Shift" => Hex1bModifiers.Shift,
                _ => Hex1bModifiers.None,
            };
            if (modifier == Hex1bModifiers.None || modifiers.HasFlag(modifier))
            {
                chord = default;
                return false;
            }

            modifiers |= modifier;
        }

        if (!TryParseKey(parts[^1], out var key))
        {
            chord = default;
            return false;
        }

        if (modifiers.HasFlag(Hex1bModifiers.Alt))
        {
            chord = default;
            return false;
        }

        chord = new WorkspaceKeyChord(key, modifiers);
        return true;
    }

    /// <summary>
    /// Gets a baseline byte identity that collapses terminal-equivalent chords.
    /// </summary>
    /// <returns>The stable byte identity used for collision validation.</returns>
    internal string GetBaselineIdentity()
    {
        var hasControl = Modifiers.HasFlag(Hex1bModifiers.Control);
        var hasAlt = Modifiers.HasFlag(Hex1bModifiers.Alt);
        var hasShift = Modifiers.HasFlag(Hex1bModifiers.Shift);
        string identity;
        if (hasControl && Key is >= Hex1bKey.A and <= Hex1bKey.Z)
        {
            identity = $"byte:{(int)Key - (int)Hex1bKey.A + 1}";
        }
        else if ((Key == Hex1bKey.Tab && !hasShift) ||
            (hasControl && Key == Hex1bKey.I))
        {
            identity = "byte:9";
        }
        else if (Key == Hex1bKey.Enter ||
            (hasControl && Key == Hex1bKey.M))
        {
            identity = "byte:13";
        }
        else if (Key == Hex1bKey.Escape ||
            (hasControl && Key == Hex1bKey.Oem4))
        {
            identity = "byte:27";
        }
        else if (!hasControl && Key is >= Hex1bKey.A and <= Hex1bKey.Z)
        {
            var character = (char)('a' + Key - Hex1bKey.A);
            identity = $"byte:{(int)(hasShift ? char.ToUpperInvariant(character) : character)}";
        }
        else if (!hasControl && Key is >= Hex1bKey.D0 and <= Hex1bKey.D9)
        {
            var digit = (int)Key - (int)Hex1bKey.D0;
            const string shiftedDigits = ")!@#$%^&*(";
            var character = hasShift ? shiftedDigits[digit] : (char)('0' + digit);
            identity = $"byte:{(int)character}";
        }
        else if (!hasControl && TryGetPunctuation(Key, hasShift, out var punctuation))
        {
            identity = $"byte:{(int)punctuation}";
        }
        else
        {
            var effectiveModifiers = Modifiers & ~Hex1bModifiers.Alt;
            identity = $"key:{Key}:{(int)effectiveModifiers}";
        }

        return hasAlt ? $"byte:27,{identity}" : identity;
    }

    /// <summary>
    /// Adds this chord as another trigger for a registered action.
    /// </summary>
    /// <param name="bindings">The bindings receiving the configured trigger.</param>
    /// <param name="actionId">The previously registered action identity.</param>
    internal void AddTrigger(InputBindingsBuilder bindings, ActionId actionId)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var step = Modifiers switch
        {
            Hex1bModifiers.None => bindings.Key(Key),
            Hex1bModifiers.Control => bindings.Ctrl().Key(Key),
            Hex1bModifiers.Alt => bindings.Alt().Key(Key),
            Hex1bModifiers.Shift => bindings.Shift().Key(Key),
            Hex1bModifiers.Control | Hex1bModifiers.Alt => bindings.Ctrl().Alt().Key(Key),
            Hex1bModifiers.Control | Hex1bModifiers.Shift => bindings.Ctrl().Shift().Key(Key),
            Hex1bModifiers.Alt | Hex1bModifiers.Shift => bindings.Alt().Shift().Key(Key),
            Hex1bModifiers.Control | Hex1bModifiers.Alt | Hex1bModifiers.Shift =>
                bindings.Ctrl().Alt().Shift().Key(Key),
            _ => throw new InvalidOperationException(
                $"Unsupported modifier value {(int)Modifiers}."),
        };
        step.Triggers(actionId);
    }

    private static bool TryParseKey(string text, out Hex1bKey key)
    {
        if (text.Length == 1 && text[0] is >= 'A' and <= 'Z')
        {
            key = Hex1bKey.A + (text[0] - 'A');
            return true;
        }

        if (text.Length == 1 && text[0] is >= '0' and <= '9')
        {
            key = Hex1bKey.D0 + (text[0] - '0');
            return true;
        }

        if (text.Length is 2 or 3 && text[0] == 'F' &&
            int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function) &&
            function is >= 1 and <= 12)
        {
            key = Hex1bKey.F1 + (function - 1);
            return true;
        }

        key = text switch
        {
            "Up" => Hex1bKey.UpArrow,
            "Down" => Hex1bKey.DownArrow,
            "Left" => Hex1bKey.LeftArrow,
            "Right" => Hex1bKey.RightArrow,
            "Home" => Hex1bKey.Home,
            "End" => Hex1bKey.End,
            "PageUp" => Hex1bKey.PageUp,
            "PageDown" => Hex1bKey.PageDown,
            "Backspace" => Hex1bKey.Backspace,
            "Delete" => Hex1bKey.Delete,
            "Insert" => Hex1bKey.Insert,
            "Tab" => Hex1bKey.Tab,
            "Enter" => Hex1bKey.Enter,
            "Space" => Hex1bKey.Spacebar,
            "Escape" => Hex1bKey.Escape,
            "," => Hex1bKey.OemComma,
            "." => Hex1bKey.OemPeriod,
            "-" => Hex1bKey.OemMinus,
            "=" => Hex1bKey.OemPlus,
            "/" => Hex1bKey.OemQuestion,
            ";" => Hex1bKey.Oem1,
            "[" => Hex1bKey.Oem4,
            "\\" => Hex1bKey.Oem5,
            "]" => Hex1bKey.Oem6,
            "'" => Hex1bKey.Oem7,
            "`" => Hex1bKey.OemTilde,
            _ => Hex1bKey.None,
        };
        return key != Hex1bKey.None;
    }

    private static bool TryGetPunctuation(Hex1bKey key, bool shifted, out char punctuation)
    {
        (var plain, var shift) = key switch
        {
            Hex1bKey.OemComma => (',', '<'),
            Hex1bKey.OemPeriod => ('.', '>'),
            Hex1bKey.OemMinus => ('-', '_'),
            Hex1bKey.OemPlus => ('=', '+'),
            Hex1bKey.OemQuestion => ('/', '?'),
            Hex1bKey.Oem1 => (';', ':'),
            Hex1bKey.Oem4 => ('[', '{'),
            Hex1bKey.Oem5 => ('\\', '|'),
            Hex1bKey.Oem6 => (']', '}'),
            Hex1bKey.Oem7 => ('\'', '"'),
            Hex1bKey.OemTilde => ('`', '~'),
            _ => ('\0', '\0'),
        };
        punctuation = shifted ? shift : plain;
        return punctuation != '\0';
    }
}
