using Avalonia.Input;

namespace HighlightCut.App;

/// <summary>A key with Ctrl/Alt/Shift, as the Keyboard settings show and save it.</summary>
public readonly record struct KeyCombo(Key Key, KeyModifiers Modifiers)
{
    private const KeyModifiers Kept = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift;

    /// <summary>Null for a modifier alone (and None/ImeProcessed/DeadCharProcessed). Meta counts as Ctrl, like the prototype.</summary>
    public static KeyCombo? From(Key key, KeyModifiers modifiers)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
            return null;
        // The numpad types the same as the main keys, as with Avalonia's own KeyGesture matching.
        key = key switch
        {
            >= Key.NumPad0 and <= Key.NumPad9 => Key.D0 + (key - Key.NumPad0),
            Key.Add => Key.OemPlus,
            Key.Subtract => Key.OemMinus,
            Key.Decimal => Key.OemPeriod,
            _ => key,
        };
        var mods = modifiers & Kept;
        if (modifiers.HasFlag(KeyModifiers.Meta))
            mods |= KeyModifiers.Control;
        return new KeyCombo(key, mods);
    }

    /// <summary>"Ctrl Shift Z", "Shift ←", "Ctrl =", "Del": the chip text.</summary>
    public string Label => Format(' ');

    /// <summary>Parts joined with <paramref name="separator"/>; '+' for the conflict line ("Ctrl+E").</summary>
    public string Format(char separator) => string.Join(separator, Parts(KeyLabel(Key)));

    /// <summary>Saved form: "Ctrl Shift Z", "Shift Left", "Ctrl OemPlus" (modifiers then the Avalonia Key name).</summary>
    public override string ToString() => string.Join(' ', Parts(KeyName(Key)));

    public static bool TryParse(string? text, out KeyCombo combo)
    {
        combo = default;
        string[] tokens = (text ?? "").Split([' ', '+'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;
        var mods = KeyModifiers.None;
        foreach (string token in tokens[..^1])
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
                mods |= KeyModifiers.Control;
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                mods |= KeyModifiers.Alt;
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                mods |= KeyModifiers.Shift;
            else
                return false;
        }
        // Enum.TryParse also takes numbers and comma lists; only a plain name is a key.
        string name = tokens[^1];
        if (!char.IsLetter(name[0]) || !name.All(char.IsLetterOrDigit) || !Enum.TryParse(name, ignoreCase: true, out Key key)
            || From(key, mods) is not { } parsed)
            return false;
        combo = parsed;
        return true;
    }

    private IEnumerable<string> Parts(string key)
    {
        if (Modifiers.HasFlag(KeyModifiers.Control))
            yield return "Ctrl";
        if (Modifiers.HasFlag(KeyModifiers.Alt))
            yield return "Alt";
        if (Modifiers.HasFlag(KeyModifiers.Shift))
            yield return "Shift";
        yield return key;
    }

    private static string KeyLabel(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Delete => "Del",
        Key.Back => "Backspace",
        Key.Enter => "Enter",
        Key.Escape => "Esc",
        Key.OemPlus => "=",
        Key.OemMinus => "−",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe or Key.OemBackslash => "\\",
        Key.OemTilde => "`",
        Key.Multiply => "Num *",
        Key.Divide => "Num /",
        _ => KeyName(key),
    };

    // Aliased Key values print their less readable name ("Return", "Oem3").
    private static string KeyName(Key key) => key switch
    {
        Key.Enter => "Enter",
        Key.OemTilde => "OemTilde",
        Key.OemOpenBrackets => "OemOpenBrackets",
        _ => key.ToString(),
    };
}
