namespace Stardate.Core;

// The keyboard: keys pressed on the host, and the BIOS's IRQ 1 handler and buffer.
public sealed partial class Machine
{
    // the BIOS keyboard buffer: 16 words at 0040:001E, a ring with head and tail at 001Ah and 001Ch
    private const ushort BufferStart = 0x1E;
    private const ushort BufferEnd = 0x3E;

    // shift-state bits in the BIOS data area at 0040:0017
    private const byte RightShift = 0x01, LeftShift = 0x02, Control = 0x04, Alt = 0x08;

    // the characters of scan codes 0 (none) to 39h, the space bar, unshifted and shifted
    private static readonly string Plain = "\0\x1B" + "1234567890-=\b\tqwertyuiop[]\r\0asdfghjkl;'`\0\\zxcvbnm,./\0*\0 ";
    private static readonly string Shifted = "\0\x1B" + "!@#$%^&*()_+\b\tQWERTYUIOP{}\r\0ASDFGHJKL:\"~\0|ZXCVBNM<>?\0*\0 ";

    private bool _extendedPrefix;

    // the keys the host holds down, as the scan code with 100h for an extended key: a saved
    // state carries them, so that loading one can let them go (see ReleaseKeys)
    private readonly HashSet<ushort> _keysDown = [];

    /// <summary>
    /// A key goes down, by its scan code (set 1, as the PC's keyboard sends it). Keys of the AT's
    /// added block, such as the separate arrows and the right Ctrl, are extended: they're sent
    /// with an E0h byte before them.
    /// </summary>
    public void PressKey(byte scanCode, bool extended = false)
    {
        if (extended)
        {
            _keyboard.Send(0xE0);
        }

        _keyboard.Send((byte)(scanCode & 0x7F));
        _keysDown.Add((ushort)((scanCode & 0x7F) | (extended ? 0x100 : 0)));
    }

    /// <summary>A key comes up: its scan code with bit 7 set.</summary>
    public void ReleaseKey(byte scanCode, bool extended = false)
    {
        if (extended)
        {
            _keyboard.Send(0xE0);
        }

        _keyboard.Send((byte)(scanCode | 0x80));
        _keysDown.Remove((ushort)((scanCode & 0x7F) | (extended ? 0x100 : 0)));
    }

    /// <summary>
    /// Lets go of every key the machine thinks is held down. After loading a saved state, the
    /// keys held when it was saved are down as far as the game knows, whatever the host's
    /// keyboard is doing now: the player lets them go, and presses again the ones still held.
    /// </summary>
    public void ReleaseKeys()
    {
        foreach (ushort key in _keysDown.ToArray())
        {
            ReleaseKey((byte)key, (key & 0x100) != 0);
        }
    }

    /// <summary>
    /// The C# half of the BIOS's IRQ 1 handler, with the scan code the ROM half read from port
    /// 60h in AL, once the keyboard intercept has passed it on: keeps the shift keys' state, and
    /// puts each key that goes down into the buffer as its scan code and character, for INT 16h.
    /// Games that read the keyboard themselves often still pass the code on to this handler.
    /// </summary>
    private void KeyboardInterrupt()
    {
        byte code = (byte)Cpu.Eax;
        if (code == 0xE0)
        {
            _extendedPrefix = true;
            return;
        }

        bool extended = _extendedPrefix;
        _extendedPrefix = false;
        bool released = (code & 0x80) != 0;
        byte scan = (byte)(code & 0x7F);
        byte shifts = ReadByte(0x40, 0x17);

        byte bit = scan switch
        {
            0x2A => LeftShift,
            0x36 => RightShift,
            0x1D => Control,
            0x38 => Alt,
            _ => 0,
        };

        if (bit != 0)
        {
            WriteByte(0x40, 0x17, released ? (byte)(shifts & ~bit) : (byte)(shifts | bit));
            return;
        }

        if (released)
        {
            return;
        }

        bool shift = (shifts & (LeftShift | RightShift)) != 0;
        int character = 0;
        if (!extended && scan < Plain.Length)
        {
            character = shift ? Shifted[scan] : Plain[scan];
        }
        else if (extended)
        {
            // the keypad's Enter and / share codes with the main keys, after E0h
            character = scan switch { 0x1C => '\r', 0x35 => '/', _ => 0 };
        }

        if ((shifts & Alt) != 0)
        {
            character = 0;
        }
        else if ((shifts & Control) != 0 && character is >= 'a' and <= 'z')
        {
            character -= 'a' - 1;
        }

        // shifted, controlled and alted function keys have codes of their own
        if (scan is >= 0x3B and <= 0x44)
        {
            if ((shifts & Alt) != 0)
            {
                scan += 0x68 - 0x3B;
            }
            else if ((shifts & Control) != 0)
            {
                scan += 0x5E - 0x3B;
            }
            else if (shift)
            {
                scan += 0x54 - 0x3B;
            }
        }

        AddToBuffer((ushort)((scan << 8) | character));
    }

    private void AddToBuffer(ushort key)
    {
        ushort tail = ReadWord(0x40, 0x1C);
        ushort next = (ushort)(tail + 2 == BufferEnd ? BufferStart : tail + 2);
        if (next == ReadWord(0x40, 0x1A))
        {
            return; // full: the PC beeps, and the key is lost
        }

        WriteWord(0x40, tail, key);
        WriteWord(0x40, 0x1C, next);
    }

    /// <summary>The next key in the BIOS buffer, taken from it or only looked at; null when it's empty.</summary>
    internal ushort? FromBuffer(bool take)
    {
        ushort head = ReadWord(0x40, 0x1A);
        if (head == ReadWord(0x40, 0x1C))
        {
            return null;
        }

        ushort key = ReadWord(0x40, head);
        if (take)
        {
            WriteWord(0x40, 0x1A, (ushort)(head + 2 == BufferEnd ? BufferStart : head + 2));
        }

        return key;
    }
}
