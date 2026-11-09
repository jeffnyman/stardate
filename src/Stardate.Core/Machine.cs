using Stardate.Core.Devices;
using Stardate.Core.Dos;
using Stardate.Core.Processors;

namespace Stardate.Core;

/// <summary>
/// The PC a game runs on: a processor, memory, the I/O ports, a color graphics adapter, the
/// speaker, and a BIOS and DOS answered in C# instead of loaded as code.
/// </summary>
/// <remarks>
/// <para>
/// The memory map is the PC's. The first 640 KB is conventional memory, holding the interrupt
/// table, the BIOS data area, DOS's memory arena and the program. The display adapter's memory
/// sits at B8000h, and the BIOS at F0000h.
/// </para>
/// <para>
/// The processor is a 386, though the games were written for the 8088: the 386 runs the 8088's
/// code as it ran then, and the program that starts <i>The Promethean Prophecy</i> from its
/// disk image, written much later, needs the 386's instructions.
/// </para>
/// <para>
/// Nothing in the machine is loaded from a real BIOS or DOS. Each interrupt they would answer
/// points at a four-byte stub in the BIOS area: the bytes <c>0F FF</c> and a service number,
/// which call into C# (see <see cref="Intel386.HostCall"/>), then <c>IRET</c>. A program sees
/// ordinary interrupt vectors, and can replace them or chain to them as it would on a real PC.
/// </para>
/// </remarks>
public sealed partial class Machine : ICpuBus
{
    /// <summary>The segment of the BIOS area, where the service stubs live.</summary>
    internal const ushort BiosSegment = 0xF000;

    // the repetitions of a string instruction between chances for an interrupt
    private const int RepeatLimit = 1024;

    /// <summary>The machine's memory: the first megabyte, all a real-mode program can reach.</summary>
    internal const uint MemorySize = 0x10_0000;

    private readonly byte[] _memory = new byte[MemorySize];

    // for each 4 KB page, whether it reads, and writes, as plain memory, which the processor
    // reaches directly: all of it, except that the BIOS's ROM can't be written
    [NotSaved]
    private readonly bool[] _plainRead = new bool[MemorySize >> 12], _plainWrite = new bool[MemorySize >> 12];
    private readonly DosDrive _drive;
    private readonly DosServices _dos;
    private readonly InterruptController _pic = new();
    private readonly IntervalTimer _timer = new();
    private readonly VideoAdapter _video;
    private readonly KeyboardController _keyboard = new();

    // the time, counted in instructions, with those the processor would have run while halted
    private long _time;

    // when counter 0 next raises IRQ 0, in timer ticks
    private long _nextIrq0 = 0x10000;

    // port 61h: the speaker's gate and data bits as last written
    private byte _portB;

    /// <summary>
    /// Makes a machine that runs programs from a game's folder, mounted as drive C:. The folder
    /// is only read: anything a program writes, saved games included, goes to the save folder,
    /// and files there take the place of the game folder's from then on.
    /// </summary>
    public Machine(string gameFolder, string saveFolder)
    {
        Cpu = new Intel386(this) { HostCall = HostCall, RepeatLimit = RepeatLimit };
        _video = new VideoAdapter(_memory);
        MapMemory();
        _timer.Reloaded0 += () => _nextIrq0 = _timer.Start0 + _timer.Period0;
        _keyboard.Interrupt += () => _pic.Raise(1);
        _drive = new DosDrive(gameFolder, saveFolder);
        _dos = new DosServices(this, _drive);
        InstallBios();
    }

    /// <summary>The processor.</summary>
    public Intel386 Cpu { get; }

    /// <summary>
    /// A line for each BIOS and DOS service a program calls, and each I/O port it uses that
    /// nothing answers yet. It's the record of what a game needs from the machine.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <summary>Whether the program has ended, by its DOS call or an <c>INT 20h</c>.</summary>
    public bool Exited { get; internal set; }

    /// <summary>The program's exit code, once it has ended.</summary>
    public int ExitCode { get; internal set; }

    /// <summary>The video mode the program last chose through the BIOS; 3, 80 by 25 color text, at the start.</summary>
    public int VideoMode => _video.Mode;

    /// <summary>
    /// Whether the text is drawn with the CGA's own 8 by 8 font, as on the PCs the games were
    /// written for, rather than the VGA's 8 by 16, as on the PCs they were mostly played on. It
    /// changes <see cref="DisplayHeight"/>, from 400 to 200.
    /// </summary>
    public bool CgaFont
    {
        get => _video.CgaFont;
        set => _video.CgaFont = value;
    }

    /// <summary>The width of the picture the display shows, in pixels: always 640.</summary>
    public int DisplayWidth => VideoAdapter.Width;

    /// <summary>The height of the picture the display shows, in pixels: 400, or 200 with <see cref="CgaFont"/>.</summary>
    public int DisplayHeight => _video.Height;

    /// <summary>
    /// Draws the picture the display shows, <see cref="DisplayWidth"/> by <see cref="DisplayHeight"/>
    /// pixels of 32-bit ARGB, row by row: text with its colors, blinking, and the cursor.
    /// </summary>
    public void Render(Span<uint> pixels)
    {
        _video.Time = Seconds;
        _video.Render(pixels);
    }

    /// <summary>
    /// The text on the screen, a line for each row, as the characters of the PC's own character
    /// set (code page 437) turned into Unicode. Nothing for a graphics mode.
    /// </summary>
    public IReadOnlyList<string> ScreenText()
    {
        if (!_video.IsText)
        {
            return [];
        }

        var lines = new string[VideoAdapter.Rows];
        int columns = _video.Columns;
        var chars = new char[columns];
        for (int row = 0; row < lines.Length; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int address = (((_video.StartAddress + (row * columns) + column) * 2) & (VideoAdapter.MemorySize - 1));
                chars[column] = CodePage437.ToChar(_memory[VideoAdapter.MemoryStart + address]);
            }

            lines[row] = new string(chars).TrimEnd();
        }

        return lines;
    }

    /// <summary>
    /// How fast the machine runs, in instructions per second. Time in the machine (the timer's
    /// count, and so the game's pace) follows the instructions run, so this is the emulated
    /// processor's speed. It's 240,000 by default, the IBM PC's 8088 at 4.77 MHz, for which the
    /// games were written and by which they time their pauses, in loops that count; DOSBox's
    /// "cycles" setting is this in thousands.
    /// </summary>
    public int InstructionsPerSecond { get; set; } = 240_000;

    /// <summary>The emulated time since the machine started.</summary>
    public TimeSpan Time => TimeSpan.FromSeconds((double)_time / InstructionsPerSecond);

    // the date and time the machine started at, the host's, from which its clock runs
    private readonly DateTime _started = DateTime.Now;

    /// <summary>
    /// The date and time in the machine, which the BIOS and DOS report: the host's when it
    /// started, and the machine's time since. It keeps pace with the instructions, as the
    /// tick count does, so a game that times itself by the clock sees the same time pass as
    /// one that counts, at any speed, and in the tests, which run faster than the host's clock.
    /// </summary>
    internal DateTime Clock => _started + Time;

    private double Seconds => (double)_time / InstructionsPerSecond;

    /// <summary>Loads a program from drive C: as DOS would, with a command tail, and readies the processor to run it.</summary>
    /// <param name="program">The program's name, as in <c>ST.EXE</c>: an MZ executable, or a .COM program.</param>
    /// <param name="arguments">What follows the program's name on the command line.</param>
    public void Load(string program, string arguments = "") => _dos.Load(program, arguments);

    /// <summary>
    /// Runs for the given number of instructions' time, or until the program exits or the
    /// processor halts with interrupts off, which nothing can end. A halted processor with
    /// interrupts on waits for the next one, and the time passes. A service the machine doesn't
    /// provide yet throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <returns>Whether the program is still running.</returns>
    public bool Run(long instructions)
    {
        long end = _time + instructions;
        while (!Exited && _time < end)
        {
            // the devices first: what's come due, and what the host did since the last run, a
            // key pressed say, then the processor until something happens
            ServiceDevices();
            if (Cpu.Halted)
            {
                if (!Cpu.InterruptsEnabled)
                {
                    return false;
                }

                // nothing happens until the next interrupt, so the time skips to it
                _time = Math.Min(NextDeviceEvent(), end);
            }
            else
            {
                // a repeated string instruction takes a step of time for each repetition
                Cpu.InterruptWaiting = _pic.HasInterrupt;
                _time += Cpu.RunFor(Math.Min(end, NextDeviceEvent()) - _time);
            }
        }

        return !Exited;
    }

    /// <summary>
    /// The devices' part between runs of instructions: the timer's interrupt if it's due, the
    /// keyboard's next scan code, and an interrupt for the processor.
    /// </summary>
    private void ServiceDevices()
    {
        UpdateTimer();
        _keyboard.Update();
        if (_pic.HasInterrupt && Cpu.InterruptsEnabled)
        {
            Cpu.Interrupt(_pic.Acknowledge());
        }
    }

    /// <summary>When the timer's next interrupt is due, in the machine's time.</summary>
    private long NextDeviceEvent()
    {
        long irq0 = _nextIrq0 == long.MaxValue ? long.MaxValue : (long)Math.Ceiling(_nextIrq0 * InstructionsPerSecond / IntervalTimer.Frequency);
        return Math.Max(_time + 1, irq0);
    }

    private long TimerTicks => (long)(_time * IntervalTimer.Frequency / InstructionsPerSecond);

    private void UpdateTimer()
    {
        long now = TimerTicks;
        _timer.Now = now;
        if (now >= _nextIrq0)
        {
            _pic.Raise(0);

            // if interrupts were off for a while, the ticks missed are lost, as on the PC
            long period = _timer.Period0;
            _nextIrq0 = _timer.Periodic0 ? _nextIrq0 + ((((now - _nextIrq0) / period) + 1) * period) : long.MaxValue;
        }
    }

    // ---- the bus -----------------------------------------------------------------------------

    /// <inheritdoc/>
    public byte Read(uint address) => _memory[address & (MemorySize - 1)];

    /// <inheritdoc/>
    public void Write(uint address, byte value)
    {
        address &= MemorySize - 1;
        if (address < 0xF_0000)
        {
            // the BIOS is ROM
            _memory[address] = value;
        }
    }

    /// <inheritdoc/>
    public byte In(ushort port)
    {
        Cpu.Stopping = true;
        _timer.Now = TimerTicks;
        switch (port)
        {
            case 0x20:
            case 0x21:
                return _pic.Read(port);
            case >= 0x40 and <= 0x43:
                return _timer.Read(port);
            case 0x61:
                // port B: the speaker bits as written, memory refresh toggling every 15 us (which
                // programs time short delays by), and counter 2's output
                return (byte)((_portB & 0x0F) | (((_timer.Now / 18) & 1) != 0 ? 0x10 : 0) | (_timer.Output2 ? 0x20 : 0));
            case 0x60:
            case 0x64:
                return _keyboard.In(port);
            case >= 0x3D0 and <= 0x3DF:
                _video.Time = Seconds;
                return _video.In(port & 0x3DF);
            default:
                LogPort(port, "in");
                return 0xFF;
        }
    }

    /// <inheritdoc/>
    public void Out(ushort port, byte value)
    {
        Cpu.Stopping = true;
        _timer.Now = TimerTicks;
        switch (port)
        {
            case 0x20:
            case 0x21:
                _pic.Write(port, value);
                break;
            case >= 0x40 and <= 0x43:
                _timer.Write(port, value);
                if (port is 0x42 or 0x43)
                {
                    UpdateSpeaker();
                }

                break;
            case 0x61:
                _portB = value;
                _timer.Gate2 = (value & 1) != 0;
                UpdateSpeaker();
                break;
            case 0x60:
            case 0x64:
                _keyboard.Out(port, value);
                break;
            case >= 0x3D0 and <= 0x3DF:
                // the CRT controller's two ports are repeated through 3D0h to 3D7h
                _video.Out(port is >= 0x3D0 and <= 0x3D7 ? 0x3D4 | (port & 1) : port, value);
                break;
            default:
                LogPort(port, $"out {value:X2}");
                break;
        }
    }

    private readonly HashSet<ushort> _portsSeen = [];

    private void LogPort(ushort port, string what)
    {
        // each port once: a game polling one would otherwise fill the log
        if (_portsSeen.Add(port))
        {
            Log?.Invoke($"port {port:X4} {what}: nothing answers yet");
        }
    }

    /// <summary>Marks the pages of plain memory for the processor: all of them, the BIOS's ROM read-only.</summary>
    private void MapMemory()
    {
        for (uint page = 0; page < _plainRead.Length; page++)
        {
            _plainRead[page] = true;
            _plainWrite[page] = (page << 12) < 0xF_0000;
        }

        Cpu.MapMemory(_memory, _plainRead, _plainWrite);
    }

    // ---- memory helpers for the services -----------------------------------------------------

    internal byte ReadByte(ushort segment, uint offset) => Read(((uint)segment << 4) + (offset & 0xFFFF));

    internal ushort ReadWord(ushort segment, uint offset) =>
        (ushort)(ReadByte(segment, offset) | (ReadByte(segment, offset + 1) << 8));

    internal void WriteByte(ushort segment, uint offset, byte value) => Write(((uint)segment << 4) + (offset & 0xFFFF), value);

    internal void WriteWord(ushort segment, uint offset, ushort value)
    {
        WriteByte(segment, offset, (byte)value);
        WriteByte(segment, offset + 1, (byte)(value >> 8));
    }

    /// <summary>Writes into the BIOS area, which programs can't.</summary>
    internal void WriteRom(uint address, ReadOnlySpan<byte> bytes) => bytes.CopyTo(_memory.AsSpan((int)address));

    /// <summary>A string ending in a zero byte, as DOS takes file names.</summary>
    internal string ReadString(ushort segment, uint offset, int max = 128)
    {
        var chars = new List<char>();
        for (int i = 0; i < max; i++)
        {
            byte b = ReadByte(segment, offset + (uint)i);
            if (b == 0)
            {
                break;
            }

            chars.Add((char)b);
        }

        return new string([.. chars]);
    }

    /// <summary>
    /// Writes bytes from a segment and offset onward, by linear address: a block longer than
    /// 64 KB, like a program's image, carries on past the segment's end instead of wrapping.
    /// </summary>
    internal void WriteBytes(ushort segment, uint offset, ReadOnlySpan<byte> bytes)
    {
        uint address = ((uint)segment << 4) + offset;
        for (int i = 0; i < bytes.Length; i++)
        {
            Write(address + (uint)i, bytes[i]);
        }
    }

    internal void Trace(string line) => Log?.Invoke(line);

    /// <summary>Closes every file the program has open on the host, for a test that's done with the machine.</summary>
    internal void CloseFiles() => _dos.CloseFiles();

    // ---- the services ------------------------------------------------------------------------

    /// <summary>
    /// Sets or clears carry in the flags an interrupt will return with. A service runs between the
    /// <c>INT</c> and the stub's <c>IRET</c>, which restores the flags the <c>INT</c> pushed, so a
    /// result reported in a flag has to be written into that pushed copy.
    /// </summary>
    internal void SetReturnFlag(uint flag, bool on)
    {
        uint sp = Cpu.Esp & 0xFFFF;
        ushort flags = ReadWord(Cpu.Ss, sp + 4);
        flags = on ? (ushort)(flags | flag) : (ushort)(flags & ~flag);
        WriteWord(Cpu.Ss, sp + 4, flags);
    }

    internal void SetReturnCarry(bool on) => SetReturnFlag(Intel386.Flag.Carry, on);

    internal void SetReturnZero(bool on) => SetReturnFlag(Intel386.Flag.Zero, on);

    /// <summary>
    /// Makes the service run again, for one that must wait (for a key, say): the processor goes
    /// back to the stub's call into C#, with interrupts on, so the keyboard's can come in while
    /// it waits, as they do while real DOS and the BIOS wait. The service is called again and
    /// again, inside its stub, until it can answer.
    /// </summary>
    /// <remarks>
    /// The wait has to stay inside the stub, rather than moving the caller's return address back
    /// over its <c>INT</c>: a program that hooks an interrupt and passes calls on with
    /// <c>PUSHF</c> and a far <c>CALL</c> didn't get there by an <c>INT</c>.
    /// </remarks>
    internal void RepeatInterrupt()
    {
        // the stub's 0F FF and service number, just run
        Cpu.Eip -= 3;
        Cpu.Eflags |= Intel386.Flag.Interrupt;
    }

    private void HostCall(byte service)
    {
        Cpu.Stopping = true;
        switch (service)
        {
            case 0x08: TimerTick(); break;
            case 0x09: KeyboardInterrupt(); break;
            case >= 0x0A and <= 0x0F: _pic.EndOfInterrupt(); break;
            case 0x10: VideoService(); break;
            case 0x11: Cpu.Eax = (Cpu.Eax & 0xFFFF_0000) | ReadWord(0x40, 0x10); break;
            case 0x12: Cpu.Eax = (Cpu.Eax & 0xFFFF_0000) | ReadWord(0x40, 0x13); break;
            case 0x15: SystemService(); break;
            case 0x16: KeyboardService(); break;
            case 0x1A: ClockService(); break;
            case 0x20: _dos.Terminate(0); break;
            case 0x21: _dos.Call(); break;
            case 0x2F: MultiplexService(); break;
            default: throw new NotSupportedException($"No service {service:X2}h.");
        }
    }
}
