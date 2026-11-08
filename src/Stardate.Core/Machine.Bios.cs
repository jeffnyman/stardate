namespace Stardate.Core;

// The BIOS: the interrupt table, the BIOS data area, and the BIOS services answered in C#.
public sealed partial class Machine
{
    // the stubs, one per vector, at F000:0100 + 4 * vector; an IRET for the rest at F000:0000
    private const ushort StubBase = 0x0100;

    // the vectors answered in C#: the BIOS's and DOS's
    private static readonly byte[] Services = [0x10, 0x11, 0x12, 0x15, 0x16, 0x1A, 0x20, 0x21, 0x2F];

    // the BIOS's timer and keyboard interrupt handlers, past the stubs, and its system
    // configuration table
    private const ushort TimerHandler = 0x0500;
    private const ushort KeyboardHandler = 0x0510;
    private const ushort ConfigurationTable = 0x0540;

    // the ticks in a day: the timer's 18.2 a second for 24 hours
    private const uint TicksPerDay = 0x1800B0;

    private void InstallBios()
    {
        const uint bios = (uint)BiosSegment << 4;

        // every vector starts at an IRET, so an interrupt nothing handles does nothing
        WriteRom(bios, [0xCF]);
        for (int vector = 0; vector < 256; vector++)
        {
            SetVector(vector, BiosSegment, 0x0000);
        }

        foreach (byte vector in Services)
        {
            ushort offset = (ushort)(StubBase + (4 * vector));
            WriteRom(bios + offset, [0x0F, 0xFF, vector, 0xCF]);
            SetVector(vector, BiosSegment, offset);
        }

        // the hardware interrupts: IRQ 0, the timer, counts the time of day and calls INT 1Ch,
        // the hook programs are meant to use; the rest only end the interrupt
        WriteRom(bios + TimerHandler, [0x0F, 0xFF, 0x08, 0xCD, 0x1C, 0xCF]);
        SetVector(0x08, BiosSegment, TimerHandler);
        foreach (int vector in Enumerable.Range(0x0A, 6))
        {
            ushort offset = (ushort)(StubBase + (4 * vector));
            WriteRom(bios + offset, [0x0F, 0xFF, (byte)vector, 0xCF]);
            SetVector(vector, BiosSegment, offset);
        }

        // IRQ 1, the keyboard, as an AT's BIOS has it: the scan code is offered to INT 15h
        // function 4Fh first, the "keyboard intercept", and only if that hands it back (carry
        // set) does the BIOS take it, into its buffer (in C#). Then the interrupt ends.
        WriteRom(bios + KeyboardHandler,
        [
            0x50,               // push ax
            0xE4, 0x60,         // in al,60h
            0xB4, 0x4F,         // mov ah,4Fh
            0xF9,               // stc
            0xCD, 0x15,         // int 15h
            0x73, 0x03,         // jnc done: the intercept kept the key
            0x0F, 0xFF, 0x09,   // the scan code in AL, to the buffer
            0xB0, 0x20,         // done: mov al,20h
            0xE6, 0x20,         // out 20h,al
            0x58,               // pop ax
            0xCF,               // iret
        ]);
        SetVector(0x09, BiosSegment, KeyboardHandler);

        // the system configuration table INT 15h function C0h points at: its length, the model
        // (FCh, an AT) and submodel, the BIOS revision, and the first feature byte: a real-time
        // clock, and INT 09h calling INT 15h function 4Fh (but no second interrupt controller)
        WriteRom(bios + ConfigurationTable, [0x08, 0x00, 0xFC, 0x00, 0x00, 0x50, 0x00, 0x00, 0x00, 0x00]);

        // the BIOS data area at 0040:0000, the parts programs read
        WriteWord(0x40, 0x10, 0x0061);   // equipment: two floppy drives, 80-column color video
        WriteWord(0x40, 0x13, 640);      // conventional memory in KB
        WriteWord(0x40, 0x1A, 0x1E);     // keyboard buffer head and tail, empty
        WriteWord(0x40, 0x1C, 0x1E);
        WriteWord(0x40, 0x63, 0x3D4);    // the CRT controller's port
        SetVideoMode(3, clear: true);

        // the model byte at F000:FFFE: FCh, an AT-class machine
        WriteRom(0xF_FFFE, [0xFC]);
    }

    /// <summary>
    /// IRQ 0's work in the BIOS: the tick count in the data area goes up, wrapping at midnight
    /// with a flag set for INT 1Ah to report. The interrupt is ended here, before the handler's
    /// INT 1Ch rather than after it, which lets a slow hook be interrupted by the next tick.
    /// </summary>
    private void TimerTick()
    {
        uint ticks = ReadWord(0x40, 0x6C) | ((uint)ReadWord(0x40, 0x6E) << 16);
        if (++ticks >= TicksPerDay)
        {
            ticks = 0;
            WriteByte(0x40, 0x70, 1);
        }

        WriteWord(0x40, 0x6C, (ushort)ticks);
        WriteWord(0x40, 0x6E, (ushort)(ticks >> 16));
        _pic.EndOfInterrupt();
    }

    internal void SetVector(int vector, ushort segment, ushort offset)
    {
        WriteWord(0, (uint)vector * 4, offset);
        WriteWord(0, ((uint)vector * 4) + 2, segment);
    }

    private byte Ah => (byte)(Cpu.Eax >> 8);

    private byte Al => (byte)Cpu.Eax;

    private void SetAx(int value) => Cpu.Eax = (Cpu.Eax & 0xFFFF_0000) | (ushort)value;

    private void SetAl(int value) => Cpu.Eax = (Cpu.Eax & 0xFFFF_FF00) | (byte)value;

    private void SetAh(int value) => Cpu.Eax = (Cpu.Eax & 0xFFFF_00FF) | ((uint)(byte)value << 8);

    private void SetBx(int value) => Cpu.Ebx = (Cpu.Ebx & 0xFFFF_0000) | (ushort)value;

    private void SetCx(int value) => Cpu.Ecx = (Cpu.Ecx & 0xFFFF_0000) | (ushort)value;

    private void SetDx(int value) => Cpu.Edx = (Cpu.Edx & 0xFFFF_0000) | (ushort)value;

    // the video BIOS is in Machine.Video.cs

    /// <summary>INT 15h, the AT's system services. Most report that they aren't supported.</summary>
    private void SystemService()
    {
        switch (Ah)
        {
            case 0x4F:
                // the keyboard intercept, unhooked: the key goes on to the BIOS as it is
                SetReturnCarry(true);
                break;
            case 0xC0:
                // the system configuration: where the table is
                Cpu.Es = BiosSegment;
                Cpu.Ebx = (Cpu.Ebx & 0xFFFF_0000) | ConfigurationTable;
                SetAh(0);
                SetReturnCarry(false);
                break;
            case 0x88:
                // extended memory in KB: none offered this way
                SetAx(0);
                SetReturnCarry(false);
                break;
            case 0x86:
                // wait a number of microseconds: returns at once
                SetReturnCarry(false);
                break;
            default:
                Trace($"15/{Ah:X2} not supported");
                SetAh(0x86);
                SetReturnCarry(true);
                break;
        }
    }

    /// <summary>INT 16h, the keyboard: keystrokes from the machine's queue.</summary>
    private void KeyboardService()
    {
        switch (Ah)
        {
            case 0x00:
            case 0x10:
                if (FromBuffer(take: true) is { } key)
                {
                    SetAx(key);
                }
                else
                {
                    RepeatInterrupt();
                }

                break;
            case 0x01:
            case 0x11:
                if (FromBuffer(take: false) is { } waiting)
                {
                    SetAx(waiting);
                    SetReturnZero(false);
                }
                else
                {
                    SetReturnZero(true);
                }

                break;
            case 0x02:
            case 0x12:
                SetAl(ReadByte(0x40, 0x17));
                break;
            case 0x03:
                // the keys' repeat rate and delay: the host's keyboard repeats keys its own way
                Trace($"16/03 typematic rate {Cpu.Ebx & 0xFFFF:X4}h");
                break;
            default:
                throw new NotSupportedException($"INT 16h function {Ah:X2}h isn't provided.");
        }
    }

    /// <summary>INT 1Ah, the clock: the BIOS tick count, and the real-time clock from the host's.</summary>
    private void ClockService()
    {
        static int Bcd(int value) => ((value / 10) << 4) | (value % 10);
        var now = DateTime.Now;
        switch (Ah)
        {
            case 0x00:
                SetCx(ReadWord(0x40, 0x6E));
                SetDx(ReadWord(0x40, 0x6C));
                SetAl(ReadByte(0x40, 0x70));
                WriteByte(0x40, 0x70, 0);
                break;
            case 0x02:
                SetCx((Bcd(now.Hour) << 8) | Bcd(now.Minute));
                SetDx(Bcd(now.Second) << 8);
                SetReturnCarry(false);
                break;
            case 0x04:
                SetCx((Bcd(now.Year / 100) << 8) | Bcd(now.Year % 100));
                SetDx((Bcd(now.Month) << 8) | Bcd(now.Day));
                SetReturnCarry(false);
                break;
            default:
                throw new NotSupportedException($"INT 1Ah function {Ah:X2}h isn't provided.");
        }
    }

    /// <summary>
    /// INT 2Fh, the multiplex interrupt, through which programs ask whether drivers and
    /// environments are present. None is: the answers leave AL at 0.
    /// </summary>
    private void MultiplexService()
    {
        Trace($"2F/{(ushort)Cpu.Eax:X4} nothing installed");
        if (Al == 0x00)
        {
            SetAl(0);
        }
    }

}
