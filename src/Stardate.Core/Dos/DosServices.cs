using System.Buffers.Binary;
using System.Text;

namespace Stardate.Core.Dos;

/// <summary>
/// DOS, answered in C#: the program loader, the memory arena, files on drive C:, and the rest of
/// INT 21h that games use. It behaves as MS-DOS 5.0 reports itself to programs.
/// </summary>
internal sealed partial class DosServices(Machine machine, DosDrive drive)
{
    /// <summary>
    /// The first paragraph of the memory arena. Real DOS, with its drivers and buffers, leaves
    /// programs from roughly 0800h to 1500h depending on the configuration; DOSBox, with almost
    /// nothing resident, from about 01A0h. Both Wing Commander games are known to need loading
    /// higher than DOSBox does (GOG starts them with LOADFIX), so the arena starts at 40 KB,
    /// which still leaves 600 KB free, as on a well-tuned DOS 5 machine.
    /// </summary>
    private const ushort ArenaStart = 0x0A00;

    /// <summary>The paragraph past conventional memory, where video memory begins.</summary>
    private const ushort ArenaEnd = 0xA000;

    private ushort _psp;
    private ushort _dtaSegment;
    private ushort _dtaOffset;
    private int _lastError;

    private readonly Registers _registers = new(machine);

    // ---- loading a program -------------------------------------------------------------------

    /// <summary>Loads a program from drive C:, and readies the processor to run it.</summary>
    public void Load(string program, string arguments)
    {
        string[] parts = Locate(program) ?? throw new FileNotFoundException($"{program} isn't on drive C:.", program);
        string dosPath = "C:\\" + DosDrive.Join(parts);
        string file = drive.FindFile(parts) ?? throw new FileNotFoundException($"{dosPath} isn't on drive C:.", dosPath);
        byte[] image = File.ReadAllBytes(file);

        InitializeArena();

        // the environment: variables, then a word of 1 and the program's full path, by which
        // programs find the folder they were run from
        var environment = new List<byte>();
        foreach (var variable in new[] { "COMSPEC=C:\\COMMAND.COM", "PATH=C:\\" })
        {
            environment.AddRange(Encoding.ASCII.GetBytes(variable));
            environment.Add(0);
        }

        environment.AddRange([0, 1, 0]);
        environment.AddRange(Encoding.ASCII.GetBytes(dosPath));
        environment.Add(0);
        ushort environmentSegment = Allocate((environment.Count + 15) / 16, 0) ?? throw new InvalidOperationException("No memory for the environment.");
        machine.WriteBytes(environmentSegment, 0, environment.ToArray());

        string name = Path.GetFileNameWithoutExtension(parts[^1]);
        if (image.Length >= 0x1C && image[0] == 'M' && image[1] == 'Z')
        {
            LoadExe(image, name, environmentSegment, arguments);
        }
        else
        {
            // whatever isn't an MZ executable is a .COM program, as DOS decides it, whatever its name
            LoadCom(image, name, environmentSegment, arguments);
        }

        machine.Trace($"loaded {dosPath} at {_psp:X4}h, {arguments}");
    }

    /// <summary>
    /// Loads a .COM program: an image of at most 64 KB less the PSP, copied to offset 100h of a
    /// segment whose first 256 bytes are the PSP, and started there with every segment register
    /// pointing at it and the stack at the top of the segment, a zero word pushed so that a
    /// <c>RET</c> reaches the PSP's <c>INT 20h</c>. A .COM program is given all the memory there is.
    /// </summary>
    private void LoadCom(byte[] image, string name, ushort environment, string arguments)
    {
        if (image.Length > 0xFF00)
        {
            throw new InvalidOperationException($"{name}.COM is {image.Length} bytes, more than a .COM program can be.");
        }

        int size = LargestFree();
        if (size < 0x1000)
        {
            throw new InvalidOperationException($"A .COM program needs 64 KB; {size * 16} bytes are free.");
        }

        ushort psp = Allocate(size, 0)!.Value;
        machine.WriteBytes(psp, 0x100, image);
        SetOwner(environment, psp, "");
        SetOwner(psp, psp, name);
        BuildPsp(psp, (ushort)(psp + size), environment, arguments);
        _psp = psp;
        _dtaSegment = psp;
        _dtaOffset = 0x80;
        machine.WriteWord(psp, 0xFFFE, 0);

        var cpu = machine.Cpu;
        cpu.Ds = cpu.Es = cpu.Ss = cpu.Cs = psp;
        cpu.Esp = 0xFFFE;
        cpu.Eip = 0x100;
        cpu.Eax = 0;
        cpu.Ebx = 0;
        cpu.Ecx = 0x00FF;
        cpu.Edx = psp;
        cpu.Esi = 0x100;
        cpu.Edi = 0xFFFE;
        cpu.Ebp = 0x091C;
        cpu.Eflags = 0x0202;
    }

    /// <summary>
    /// Loads an MZ executable. Its header gives the size of the image, the relocations to apply,
    /// the memory it needs beyond the image, and its starting CS:IP and SS:SP, all relative to
    /// the segment the image lands at, just past the 256-byte program segment prefix.
    /// </summary>
    private void LoadExe(byte[] file, string name, ushort environment, string arguments)
    {
        ushort Word(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(offset));

        int lastPage = Word(0x02), pages = Word(0x04), relocations = Word(0x06), headerParagraphs = Word(0x08);
        int minimum = Word(0x0A), maximum = Word(0x0C);
        int fileSize = lastPage == 0 ? pages * 512 : ((pages - 1) * 512) + lastPage;
        int imageStart = headerParagraphs * 16;
        int imageSize = Math.Min(fileSize, file.Length) - imageStart;
        int imageParagraphs = (imageSize + 15) / 16;

        // the program takes the largest block there is, up to what its header asks for at most
        int largest = LargestFree();
        int needed = 0x10 + imageParagraphs + minimum;
        if (largest < needed)
        {
            throw new InvalidOperationException($"The program needs {needed * 16} bytes; {largest * 16} are free.");
        }

        int size = Math.Min(largest, 0x10 + imageParagraphs + maximum);
        ushort psp = Allocate(size, 0)!.Value;
        ushort loadSegment = (ushort)(psp + 0x10);
        machine.WriteBytes(loadSegment, 0, file.AsSpan(imageStart, imageSize));

        // each relocation is a far pointer to a word in the image that holds a segment, relative
        // to the image's start: the load segment is added to it
        int table = Word(0x18);
        for (int i = 0; i < relocations; i++)
        {
            ushort offset = Word(table + (4 * i)), segment = Word(table + (4 * i) + 2);
            ushort at = (ushort)(loadSegment + segment);
            machine.WriteWord(at, offset, (ushort)(machine.ReadWord(at, offset) + loadSegment));
        }

        SetOwner(environment, psp, "");
        SetOwner(psp, psp, name);
        BuildPsp(psp, (ushort)(psp + size), environment, arguments);
        _psp = psp;
        _dtaSegment = psp;
        _dtaOffset = 0x80;

        var cpu = machine.Cpu;
        cpu.Ds = cpu.Es = psp;
        cpu.Ss = (ushort)(loadSegment + Word(0x0E));
        cpu.Esp = Word(0x10);
        cpu.Cs = (ushort)(loadSegment + Word(0x16));
        cpu.Eip = Word(0x14);
        cpu.Eax = 0;
        cpu.Ebx = 0;
        cpu.Ecx = 0x00FF;
        cpu.Edx = psp;
        cpu.Esi = cpu.Eip;
        cpu.Edi = cpu.Esp;
        cpu.Ebp = 0x091C;
        cpu.Eflags = 0x0202;
    }

    /// <summary>
    /// The program segment prefix: the 256 bytes DOS puts before every program, holding how to
    /// exit, how much memory it has, its environment, its file handles and its command line.
    /// </summary>
    private void BuildPsp(ushort psp, ushort end, ushort environment, string arguments)
    {
        machine.WriteBytes(psp, 0, new byte[0x100]);
        machine.WriteBytes(psp, 0x00, [0xCD, 0x20]);                 // INT 20h, the old way to exit
        machine.WriteWord(psp, 0x02, end);                            // the paragraph past its memory
        for (uint i = 0; i < 3; i++)
        {
            // the terminate, Ctrl-Break and critical-error vectors (22h to 24h) as they were
            machine.WriteWord(psp, 0x0A + (4 * i), machine.ReadWord(0, (0x22 + i) * 4));
            machine.WriteWord(psp, 0x0C + (4 * i), machine.ReadWord(0, ((0x22 + i) * 4) + 2));
        }

        machine.WriteWord(psp, 0x16, psp);                            // the parent: itself, as for a shell
        machine.WriteBytes(psp, 0x18, [1, 1, 1, 0, 2, .. Enumerable.Repeat((byte)0xFF, 15)]);
        machine.WriteWord(psp, 0x2C, environment);
        machine.WriteWord(psp, 0x32, 20);                             // the handle table's size and place
        machine.WriteWord(psp, 0x34, 0x18);
        machine.WriteWord(psp, 0x36, psp);
        machine.WriteBytes(psp, 0x50, [0xCD, 0x21, 0xCB]);           // INT 21h, RETF
        machine.WriteBytes(psp, 0x5C, [0, .. Enumerable.Repeat((byte)' ', 11)]);
        machine.WriteBytes(psp, 0x6C, [0, .. Enumerable.Repeat((byte)' ', 11)]);

        string tail = arguments.Length > 0 ? " " + arguments : "";
        tail = tail[..Math.Min(tail.Length, 126)];
        machine.WriteByte(psp, 0x80, (byte)tail.Length);
        machine.WriteBytes(psp, 0x81, [.. Encoding.ASCII.GetBytes(tail), 0x0D]);
    }

    /// <summary>Ends the program, as DOS does: its files are closed, and the machine has ended.</summary>
    public void Terminate(int code)
    {
        CloseProgramFiles();
        machine.ExitCode = code;
        machine.Trace($"exit {code}");
        machine.Exited = true;
    }

    // ---- the memory arena --------------------------------------------------------------------

    // Memory is a chain of blocks, each led by a 16-byte memory control block: 'M' (or 'Z' for the
    // last), the owning program's PSP (0 when free), the size in paragraphs, and a name.

    private void InitializeArena() => WriteBlock(ArenaStart, 'Z', 0, ArenaEnd - ArenaStart - 1);

    private void WriteBlock(int mcb, char type, int owner, int size)
    {
        machine.WriteByte((ushort)mcb, 0, (byte)type);
        machine.WriteWord((ushort)mcb, 1, (ushort)owner);
        machine.WriteWord((ushort)mcb, 3, (ushort)size);
    }

    private IEnumerable<(ushort Mcb, char Type, ushort Owner, ushort Size)> Blocks()
    {
        ushort mcb = ArenaStart;
        while (true)
        {
            char type = (char)machine.ReadByte(mcb, 0);
            ushort size = machine.ReadWord(mcb, 3);
            yield return (mcb, type, machine.ReadWord(mcb, 1), size);
            if (type != 'M')
            {
                yield break;
            }

            mcb = (ushort)(mcb + size + 1);
        }
    }

    private int LargestFree() => Blocks().Where(b => b.Owner == 0).Select(b => (int)b.Size).DefaultIfEmpty(0).Max();

    /// <summary>Allocates a block of paragraphs by first fit, returning its segment, or null.</summary>
    private ushort? Allocate(int paragraphs, ushort owner)
    {
        foreach (var block in Blocks())
        {
            if (block.Owner != 0 || block.Size < paragraphs)
            {
                continue;
            }

            if (block.Size > paragraphs)
            {
                // split off the rest as a free block
                WriteBlock(block.Mcb + paragraphs + 1, block.Type, 0, block.Size - paragraphs - 1);
                WriteBlock(block.Mcb, 'M', owner == 0 ? 8 : owner, paragraphs);
            }
            else
            {
                WriteBlock(block.Mcb, block.Type, owner == 0 ? 8 : owner, paragraphs);
            }

            return (ushort)(block.Mcb + 1);
        }

        return null;
    }

    private void SetOwner(ushort segment, ushort owner, string name)
    {
        ushort mcb = (ushort)(segment - 1);
        machine.WriteWord(mcb, 1, owner);
        var bytes = new byte[8];
        Encoding.ASCII.GetBytes(name.ToUpperInvariant()[..Math.Min(name.Length, 8)]).CopyTo(bytes, 0);
        machine.WriteBytes(mcb, 8, bytes);
    }

    private bool IsBlock(ushort segment) =>
        Blocks().Any(b => b.Mcb == segment - 1 && b.Owner != 0);

    private void Free(ushort segment)
    {
        machine.WriteWord((ushort)(segment - 1), 1, 0);
        Coalesce();
    }

    /// <summary>Joins each run of free blocks into one.</summary>
    private void Coalesce()
    {
        var blocks = Blocks().ToList();
        for (int i = 0; i < blocks.Count - 1; i++)
        {
            if (blocks[i].Owner == 0 && blocks[i + 1].Owner == 0)
            {
                var next = blocks[i + 1];
                WriteBlock(blocks[i].Mcb, next.Type, 0, blocks[i].Size + next.Size + 1);
                blocks[i] = (blocks[i].Mcb, next.Type, 0, (ushort)(blocks[i].Size + next.Size + 1));
                blocks.RemoveAt(i + 1);
                i--;
            }
        }
    }

    /// <summary>Grows or shrinks a block in place; when it can't grow enough, returns the most it could have.</summary>
    private int? Resize(ushort segment, int paragraphs)
    {
        ushort mcb = (ushort)(segment - 1);
        var blocks = Blocks().ToList();
        int index = blocks.FindIndex(b => b.Mcb == mcb);
        var block = blocks[index];

        // what it could reach: itself and a free block after it
        int available = block.Size;
        char lastType = block.Type;
        if (index + 1 < blocks.Count && blocks[index + 1].Owner == 0)
        {
            available += blocks[index + 1].Size + 1;
            lastType = blocks[index + 1].Type;
        }

        if (paragraphs > available)
        {
            return available;
        }

        if (paragraphs < available)
        {
            WriteBlock(mcb + paragraphs + 1, lastType, 0, available - paragraphs - 1);
            WriteBlock(mcb, 'M', block.Owner, paragraphs);
            Coalesce();
        }
        else
        {
            WriteBlock(mcb, lastType, block.Owner, paragraphs);
        }

        return null;
    }

    // ---- INT 21h -----------------------------------------------------------------------------

    // the scan code still to come after the zero that DOS gives for a key with no character
    private byte? _extendedKey;

    private void Ok() => machine.SetReturnCarry(false);

    private void Fail(int error)
    {
        _lastError = error;
        _registers.Ax = (ushort)error;
        machine.SetReturnCarry(true);
    }

    public void Call()
    {
        var cpu = _registers;
        int function = cpu.Ah;
        switch (function)
        {
            case 0x00: Terminate(0); break;
            case 0x01:
            case 0x07:
            case 0x08:
                ReadKey(echo: function == 0x01);
                break;
            case 0x02: ConsoleOut((char)cpu.Dl); break;
            case 0x06:
                if (cpu.Dl == 0xFF)
                {
                    // direct console input: a key if one is waiting, without waiting for one
                    if (NextKey() is { } key)
                    {
                        cpu.Al = key;
                        machine.SetReturnZero(false);
                    }
                    else
                    {
                        cpu.Al = 0;
                        machine.SetReturnZero(true);
                    }
                }
                else
                {
                    ConsoleOut((char)cpu.Dl);
                }

                break;
            case 0x09:
                for (uint i = 0; i < 4096; i++)
                {
                    char c = (char)machine.ReadByte(cpu.Ds, cpu.Dx + i);
                    if (c == '$')
                    {
                        break;
                    }

                    ConsoleOut(c);
                }

                break;
            case 0x0B:
                // whether a key is waiting
                cpu.Al = _extendedKey is not null || machine.FromBuffer(take: false) is not null ? (byte)0xFF : (byte)0;
                break;
            case 0x0C:
                // flush the keyboard, then do the input function in AL
                _extendedKey = null;
                while (machine.FromBuffer(take: true) is not null)
                {
                }

                if (cpu.Al is 0x01 or 0x06 or 0x07 or 0x08 or 0x0A)
                {
                    cpu.Ah = cpu.Al;
                    Call();
                }

                break;
            case 0x0D: break;
            case 0x0E:
                // select a drive (DL, 0 for A:): C: is the only one, and stays the current drive
                cpu.Al = LastDrive;
                break;
            case 0x19: cpu.Al = DriveC; break;
            case 0x1A:
                _dtaSegment = cpu.Ds;
                _dtaOffset = cpu.Dx;
                break;
            case 0x25:
                machine.SetVector(cpu.Al, cpu.Ds, cpu.Dx);
                machine.Trace($"21/25 set vector {cpu.Al:X2}h to {cpu.Ds:X4}:{cpu.Dx:X4}");
                break;
            case 0x2A:
            {
                var now = machine.Clock;
                cpu.Cx = (ushort)now.Year;
                cpu.Dh = (byte)now.Month;
                cpu.Dl = (byte)now.Day;
                cpu.Al = (byte)now.DayOfWeek;
                break;
            }

            case 0x2B:
            case 0x2D:
                cpu.Al = 0;
                break;
            case 0x2C:
            {
                var now = machine.Clock;
                cpu.Ch = (byte)now.Hour;
                cpu.Cl = (byte)now.Minute;
                cpu.Dh = (byte)now.Second;
                cpu.Dl = (byte)(now.Millisecond / 10);
                break;
            }

            case 0x2F:
                cpu.Es = _dtaSegment;
                cpu.Bx = _dtaOffset;
                break;
            case 0x30:
                // MS-DOS 5.0
                cpu.Ax = 0x0005;
                cpu.Bx = 0xFF00;
                cpu.Cx = 0;
                machine.Trace("21/30 DOS version 5.0");
                break;
            case 0x33:
                switch (cpu.Al)
                {
                    case 0x00: cpu.Dl = 0; break;
                    case 0x05: cpu.Dl = 3; break;
                    case 0x06: cpu.Bx = 0x0005; cpu.Dx = 0; break;
                }

                break;
            case 0x35:
                cpu.Bx = machine.ReadWord(0, (uint)cpu.Al * 4);
                cpu.Es = machine.ReadWord(0, ((uint)cpu.Al * 4) + 2);
                machine.Trace($"21/35 get vector {cpu.Al:X2}h: {cpu.Es:X4}:{cpu.Bx:X4}");
                break;
            case 0x36:
                if (!DriveExists(DriveIn(cpu.Dl)))
                {
                    cpu.Ax = 0xFFFF;
                }
                else
                {
                    // a drive of 8-sector clusters of 512 bytes, about 128 MB of it free
                    cpu.Ax = 8;
                    cpu.Bx = 0x7FFF;
                    cpu.Cx = 512;
                    cpu.Dx = 0xFFFF;
                }

                break;
            case 0x37:
                if (cpu.Al == 0)
                {
                    cpu.Dl = (byte)'/';
                }

                cpu.Al = 0;
                break;
            case 0x38:
                machine.WriteBytes(cpu.Ds, cpu.Dx, new byte[34]);
                cpu.Bx = 1;
                Ok();
                break;
            case 0x48: AllocateCall(cpu); break;
            case 0x49: FreeCall(cpu); break;
            case 0x4A: ResizeCall(cpu); break;
            case 0x4C: Terminate(cpu.Al); break;
            case 0x4D: cpu.Ax = 0; break;
            case 0x50: _psp = cpu.Bx; break;
            case 0x51:
            case 0x62:
                cpu.Bx = _psp;
                break;
            case 0x54: cpu.Al = 0; break;
            case 0x58:
                if (cpu.Al is 0x00 or 0x02)
                {
                    cpu.Ax = 0;
                }

                Ok();
                break;
            case 0x59:
                cpu.Ax = (ushort)_lastError;
                cpu.Bx = 0x0104;
                cpu.Ch = 1;
                break;
            case 0x66 when cpu.Al == 0x01:
                // the code page, active and the system's: 437, the PC's own
                cpu.Bx = 437;
                cpu.Dx = 437;
                Ok();
                break;
            case 0x67:
            case 0x68:
                Ok();
                break;
            default:
                if (!FileCall(function, cpu))
                {
                    throw new NotSupportedException($"INT 21h function {function:X2}h (AL={cpu.Al:X2}h) isn't provided.");
                }

                break;
        }
    }

    private void AllocateCall(Registers cpu)
    {
        if (Allocate(cpu.Bx, _psp) is { } segment)
        {
            cpu.Ax = segment;
            Ok();
            machine.Trace($"21/48 allocate {cpu.Bx * 16} bytes: {segment:X4}h");
        }
        else
        {
            int largest = LargestFree();
            machine.Trace($"21/48 allocate {cpu.Bx * 16} bytes: only {largest * 16} free");
            Fail(8);
            cpu.Bx = (ushort)largest;
        }
    }

    private void FreeCall(Registers cpu)
    {
        if (IsBlock(cpu.Es))
        {
            Free(cpu.Es);
            Ok();
            machine.Trace($"21/49 free {cpu.Es:X4}h");
        }
        else
        {
            Fail(9);
        }
    }

    private void ResizeCall(Registers cpu)
    {
        if (!IsBlock(cpu.Es))
        {
            Fail(9);
            return;
        }

        if (Resize(cpu.Es, cpu.Bx) is { } most)
        {
            machine.Trace($"21/4A resize {cpu.Es:X4}h to {cpu.Bx * 16} bytes: at most {most * 16}");
            Fail(8);
            cpu.Bx = (ushort)most;
        }
        else
        {
            machine.Trace($"21/4A resize {cpu.Es:X4}h to {cpu.Bx * 16} bytes");
            Ok();
        }
    }

    /// <summary>
    /// Functions 01h, 07h and 08h: the next key's character, waiting for one. DOS reads through
    /// the BIOS's buffer; until a key is there, the call repeats, with interrupts able to come
    /// in, so the key the program waits for can arrive.
    /// </summary>
    private void ReadKey(bool echo)
    {
        if (NextKey() is not { } key)
        {
            machine.RepeatInterrupt();
            return;
        }

        _registers.Al = key;
        if (echo && key != 0)
        {
            ConsoleOut((char)key);
        }
    }

    /// <summary>
    /// The next character from the keyboard, or null when none is waiting. A key with no
    /// character (an arrow, a function key) comes as two: a zero, then its scan code at the next
    /// call, as DOS gives them.
    /// </summary>
    private byte? NextKey()
    {
        if (_extendedKey is { } scan)
        {
            _extendedKey = null;
            return scan;
        }

        if (machine.FromBuffer(take: true) is not { } key)
        {
            return null;
        }

        byte character = (byte)key;
        if (character is 0 or 0xE0)
        {
            _extendedKey = (byte)(key >> 8);
            return 0;
        }

        return character;
    }

    /// <summary>A character to the console: DOS's CON writes it through the BIOS, onto the screen.</summary>
    private void ConsoleOut(char c) => machine.Teletype((byte)c);
}
