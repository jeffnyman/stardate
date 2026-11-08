namespace Stardate.Core;

/// <summary>
/// The PC keyboard's scan codes (set 1, as the controller passes them to programs), from the
/// USB HID usage numbers that today's keyboards, and SDL's scan codes, use.
/// </summary>
/// <remarks>
/// A scan code names a key's place on the keyboard, not its letter: 1Eh is the key right of
/// Caps Lock, an A on a US keyboard and a Q on a French one. The usages are places too, so the
/// table matches them place for place, and a game sees its keys where its manual puts them.
/// </remarks>
public static class ScanCodes
{
    // set 1 codes by usage, from 04h (A); 0 for none. Bit 8 marks the keys sent after E0h.
    private const int E0 = 0x100;

    private static readonly ushort[] Codes = Build();

    /// <summary>
    /// The scan code for a USB HID keyboard usage, and whether it's one of the AT's extended keys
    /// (sent after an E0h byte); false for keys a PC of the time didn't have, such as the
    /// Windows keys.
    /// </summary>
    public static bool TryFromUsb(int usage, out byte scanCode, out bool extended)
    {
        int code = usage is >= 0 and < 0x100 ? Codes[usage] : 0;
        scanCode = (byte)code;
        extended = (code & E0) != 0;
        return code != 0;
    }

    private static ushort[] Build()
    {
        var codes = new ushort[0x100];

        // A to Z
        byte[] letters =
        [
            0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
            0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
        ];
        for (int i = 0; i < 26; i++)
        {
            codes[0x04 + i] = letters[i];
        }

        // 1 to 9, then 0: the top row runs 02h to 0Bh
        for (int i = 0; i < 10; i++)
        {
            codes[0x1E + i] = (ushort)(0x02 + i);
        }

        // Enter, Esc, Backspace, Tab, Space, - = [ ] \ (and the non-US # beside Enter), ; ' ` , . /
        // and Caps Lock
        ushort[] row = [0x1C, 0x01, 0x0E, 0x0F, 0x39, 0x0C, 0x0D, 0x1A, 0x1B, 0x2B, 0x2B, 0x27, 0x28, 0x29, 0x33, 0x34, 0x35, 0x3A];
        row.CopyTo(codes, 0x28);

        // F1 to F10 run 3Bh to 44h; F11 and F12 came later, at 57h and 58h
        for (int i = 0; i < 10; i++)
        {
            codes[0x3A + i] = (ushort)(0x3B + i);
        }

        codes[0x44] = 0x57;
        codes[0x45] = 0x58;

        // Print Screen, Scroll Lock (Pause sends a sequence of its own and is left out)
        codes[0x46] = E0 | 0x37;
        codes[0x47] = 0x46;

        // the AT's separate block: Insert, Home, Page Up, Delete, End, Page Down, and the arrows
        // right, left, down, up; all extended, as they share codes with the keypad's
        ushort[] block = [0x52, 0x47, 0x49, 0x53, 0x4F, 0x51, 0x4D, 0x4B, 0x50, 0x48];
        for (int i = 0; i < block.Length; i++)
        {
            codes[0x49 + i] = (ushort)(E0 | block[i]);
        }

        // the keypad: Num Lock, / * - + Enter, 1 to 9, 0 and the point
        ushort[] keypad = [0x45, E0 | 0x35, 0x37, 0x4A, 0x4E, E0 | 0x1C, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49, 0x52, 0x53];
        keypad.CopyTo(codes, 0x53);

        // the key left of Z on keyboards outside the US
        codes[0x64] = 0x56;

        // left Ctrl, Shift, Alt; then right Ctrl, Shift, Alt (the GUI keys between are left out)
        codes[0xE0] = 0x1D;
        codes[0xE1] = 0x2A;
        codes[0xE2] = 0x38;
        codes[0xE4] = E0 | 0x1D;
        codes[0xE5] = 0x36;
        codes[0xE6] = E0 | 0x38;
        return codes;
    }
}
