namespace Stardate.Core.Tests;

/// <summary>Typing on the machine's keyboard, as a player would, for the tests.</summary>
internal static class Keys
{
    private const byte LeftShift = 0x2A;

    // the US keyboard's characters by scan code, unshifted and shifted
    private static readonly string Plain = "\0\x1B" + "1234567890-=\b\tqwertyuiop[]\n\0asdfghjkl;'`\0\\zxcvbnm,./\0*\0 ";
    private static readonly string Shifted = "\0\x1B" + "!@#$%^&*()_+\b\tQWERTYUIOP{}\n\0ASDFGHJKL:\"~\0|ZXCVBNM<>?\0*\0 ";

    /// <summary>Types text, a key at a time with a moment between, shifting for capitals and the like.</summary>
    public static void Type(Machine machine, string text)
    {
        foreach (char c in text)
        {
            int plain = Plain.IndexOf(c), shifted = Shifted.IndexOf(c);
            if (plain > 0)
            {
                Press(machine, (byte)plain);
            }
            else if (shifted > 0)
            {
                machine.PressKey(LeftShift);
                Press(machine, (byte)shifted);
                machine.ReleaseKey(LeftShift);
            }
            else
            {
                throw new ArgumentException($"There's no key for '{c}'.", nameof(text));
            }
        }
    }

    /// <summary>Presses a key by its scan code, and lets it go, with time for the game to see each.</summary>
    public static void Press(Machine machine, byte scanCode)
    {
        machine.PressKey(scanCode);
        machine.Run(machine.InstructionsPerSecond / 50);
        machine.ReleaseKey(scanCode);
        machine.Run(machine.InstructionsPerSecond / 15);
    }
}
