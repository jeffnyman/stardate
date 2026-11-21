namespace Stardate.Driver;

/// <summary>The PC keyboard's scan codes, for the keys and characters a script types.</summary>
internal static class Keys
{
    public const byte Escape = 0x01;
    public const byte Backspace = 0x0E;
    public const byte Enter = 0x1C;
    public const byte LeftShift = 0x2A;
    public const byte Space = 0x39;

    // the US keyboard's characters by scan code, unshifted and shifted
    private const string Plain = "\0\x1B" + "1234567890-=\b\tqwertyuiop[]\n\0asdfghjkl;'`\0\\zxcvbnm,./\0*\0 ";
    private const string Shifted = "\0\x1B" + "!@#$%^&*()_+\b\tQWERTYUIOP{}\n\0ASDFGHJKL:\"~\0|ZXCVBNM<>?\0*\0 ";

    /// <summary>The keys a script names in braces, as <c>{enter}</c> or <c>{f5}</c>: their scan codes, and whether they're the extended (E0) kind.</summary>
    public static readonly IReadOnlyDictionary<string, (byte ScanCode, bool Extended)> Named = BuildNamed();

    /// <summary>The key that types a character, and whether it needs Shift; null when there's none.</summary>
    public static (byte ScanCode, bool Shift)? ForChar(char ch)
    {
        int plain = Plain.IndexOf(ch);
        if (plain > 0)
        {
            return ((byte)plain, false);
        }

        int shifted = Shifted.IndexOf(ch);
        return shifted > 0 ? ((byte)shifted, true) : null;
    }

    private static Dictionary<string, (byte, bool)> BuildNamed()
    {
        var keys = new Dictionary<string, (byte, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["enter"] = (Enter, false), ["esc"] = (Escape, false), ["space"] = (Space, false), ["bksp"] = (Backspace, false),
            ["up"] = (0x48, true), ["down"] = (0x50, true), ["left"] = (0x4B, true), ["right"] = (0x4D, true),
            ["pgup"] = (0x49, true), ["pgdn"] = (0x51, true), ["home"] = (0x47, true), ["end"] = (0x4F, true),
        };
        for (int i = 1; i <= 10; i++)
        {
            keys[$"f{i}"] = ((byte)(0x3A + i), false);
        }

        return keys;
    }
}
