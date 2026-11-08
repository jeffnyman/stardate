namespace Stardate.Core.Processors;

/// <summary>
/// The Intel 80386, run one instruction at a time: real mode, with every instruction a real-mode
/// program can use apart from the floating-point unit's, and protected mode without paging,
/// virtual-8086 mode or task switches.
/// </summary>
/// <remarks>
/// <para>
/// Real mode is the 8086's model of memory: a segment register holds a paragraph number, the
/// segment starts at that number times 16, and every offset into it is checked against a 64 KB
/// limit. On the 386 the 32-bit registers, the FS and GS segments, the 386's new instructions and
/// its 32-bit addressing all still work there, by way of the operand-size (<c>66h</c>) and
/// address-size (<c>67h</c>) prefixes.
/// </para>
/// <para>
/// Protected mode is in <c>Intel386.Protected.cs</c>: segments described by tables in memory,
/// 32-bit code and stacks, four privilege levels, and interrupts through a table of gates.
/// </para>
/// <para>
/// Each <see cref="Step"/> runs one whole instruction, prefixes and all; a repeated string
/// instruction runs every repetition. The processor isn't cycle-accurate: the games this core is
/// built for ran on PCs of many speeds and don't depend on instruction timing.
/// </para>
/// <para>
/// Faults behave as on the chip: the instruction's changes to the registers are undone, and the
/// processor enters the exception's handler with the faulting instruction's address on the stack,
/// so the handler can retry it.
/// </para>
/// <para>
/// It is checked against 1.7 million instructions captured from a real 386 (the SingleStepTests
/// suite), and where Intel's manuals leave a result undefined it mostly does what that chip did:
/// the flags of shifts by large counts, 16-bit SHLD and SHRD past 16, and the 386's quirks in
/// POPAD, PUSHAD and SIB addressing. It doesn't model the prefetch queue, the IDIV overflows the
/// 386 fails to detect, or the flags AAM leaves when dividing by zero.
/// </para>
/// </remarks>
public sealed partial class Intel386
{
    // the general registers, in the order instructions number them
    internal const int EAX = 0, ECX = 1, EDX = 2, EBX = 3, ESP = 4, EBP = 5, ESI = 6, EDI = 7;

    // the segment registers, in the order instructions number them
    internal const int ES = 0, CS = 1, SS = 2, DS = 3, FS = 4, GS = 5;

    private readonly ICpuBus _bus;

    // the bus's plain memory, for the processor to read and write directly: the memory, and for
    // each 4 KB page of it whether it reads (and writes) as nothing but that memory, with no device
    // or ROM in the way. Elsewhere, and with no map, every byte goes through the bus.
    [NotSaved]
    private byte[]? _ram;
    [NotSaved]
    private bool[]? _plainRead;
    [NotSaved]
    private bool[]? _plainWrite;
    private readonly uint[] _regs = new uint[8];
    private readonly ushort[] _selectors = new ushort[6];

    // each segment's descriptor cache: in real mode a base of 16 times the selector and a 64 KB limit
    private readonly uint[] _bases = new uint[6];
    private readonly uint[] _limits = [0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF];

    // and the rest of it: the descriptor's access byte, whether the segment is 32-bit (code that
    // defaults to 32-bit operands and addresses, or a stack addressed by ESP), and what may be done
    // through it. After reset each is a writable 16-bit data segment, CS a readable code segment
    private readonly byte[] _access = [0x93, 0x9B, 0x93, 0x93, 0x93, 0x93];
    private readonly bool[] _big = new bool[6];
    private readonly SegmentUse[] _use = [SegmentUse.ReadWrite, SegmentUse.ReadWrite, SegmentUse.ReadWrite, SegmentUse.ReadWrite, SegmentUse.ReadWrite, SegmentUse.ReadWrite];

    private uint _eip;
    private uint _flags = 0x0002;

    // the instruction in progress: where it began, the stack pointer then (both restored when it
    // faults), how many bytes it has taken, and what its prefixes chose
    private uint _start;
    private uint _startEsp;
    private int _length;
    private bool _operand32;
    private bool _address32;
    private int _segment;
    private Repeat _repeat;

    private enum Repeat : byte
    {
        None,
        WhileEqual,
        WhileNotEqual,
    }

    /// <summary>What may be done through a segment register.</summary>
    [Flags]
    internal enum SegmentUse : byte
    {
        // a null selector in a data segment register: loading it is allowed, using it isn't
        None = 0,
        Read = 1,
        Write = 2,
        ReadWrite = Read | Write,

        // offsets valid from above the limit to the top, for stacks that grow down
        ExpandDown = 4,
    }

    /// <summary>
    /// Gives the processor the bus's memory to reach directly where a page is plain memory: a
    /// page's entry in <paramref name="plainRead"/> says it reads as <paramref name="ram"/> and
    /// nothing else, in <paramref name="plainWrite"/> that it writes so. The bus changes the maps
    /// as its memory changes, the A20 gate say.
    /// </summary>
    internal void MapMemory(byte[] ram, bool[] plainRead, bool[] plainWrite)
    {
        _ram = ram;
        _plainRead = plainRead;
        _plainWrite = plainWrite;
    }

    /// <summary>Makes a processor on the bus, in real mode, as the 386 is after reset.</summary>
    public Intel386(ICpuBus bus)
    {
        _bus = bus;
        Cs = 0xF000;
        Eip = 0xFFF0;
    }

    /// <summary>EAX, the accumulator.</summary>
    public uint Eax { get => _regs[EAX]; set => _regs[EAX] = value; }

    /// <summary>ECX, the count register.</summary>
    public uint Ecx { get => _regs[ECX]; set => _regs[ECX] = value; }

    /// <summary>EDX, the data register.</summary>
    public uint Edx { get => _regs[EDX]; set => _regs[EDX] = value; }

    /// <summary>EBX, the base register.</summary>
    public uint Ebx { get => _regs[EBX]; set => _regs[EBX] = value; }

    /// <summary>ESP, the stack pointer. In real mode only its low 16 bits, SP, address the stack.</summary>
    public uint Esp { get => _regs[ESP]; set => _regs[ESP] = value; }

    /// <summary>EBP, the base pointer.</summary>
    public uint Ebp { get => _regs[EBP]; set => _regs[EBP] = value; }

    /// <summary>ESI, the source index.</summary>
    public uint Esi { get => _regs[ESI]; set => _regs[ESI] = value; }

    /// <summary>EDI, the destination index.</summary>
    public uint Edi { get => _regs[EDI]; set => _regs[EDI] = value; }

    /// <summary>The instruction pointer, an offset into the code segment.</summary>
    public uint Eip { get => _eip; set => _eip = value; }

    /// <summary>
    /// The flags. Setting them keeps bit 1, which always reads as set, and clears bits 3, 5 and 15,
    /// which always read as clear.
    /// </summary>
    public uint Eflags { get => _flags; set => _flags = (value & ~0x8028u) | 0x0002; }

    /// <summary>The code segment. Setting a segment register loads its base as in real mode.</summary>
    public ushort Cs { get => _selectors[CS]; set => LoadSegment(CS, value); }

    /// <summary>The stack segment.</summary>
    public ushort Ss { get => _selectors[SS]; set => LoadSegment(SS, value); }

    /// <summary>The data segment, the default for most memory operands.</summary>
    public ushort Ds { get => _selectors[DS]; set => LoadSegment(DS, value); }

    /// <summary>The extra segment, the destination of the string instructions.</summary>
    public ushort Es { get => _selectors[ES]; set => LoadSegment(ES, value); }

    /// <summary>FS, a data segment the 386 added.</summary>
    public ushort Fs { get => _selectors[FS]; set => LoadSegment(FS, value); }

    /// <summary>GS, a data segment the 386 added.</summary>
    public ushort Gs { get => _selectors[GS]; set => LoadSegment(GS, value); }

    /// <summary>Control register 0. Its lowest bit, PE, would switch to protected mode.</summary>
    public uint Cr0 { get; set; } = 0x0000_0010;

    /// <summary>Control register 3, the page directory's address when paging is on.</summary>
    public uint Cr3 { get; set; }

    /// <summary>Debug register 6, the debug status.</summary>
    public uint Dr6 { get; set; } = 0xFFFF_0FF0;

    /// <summary>Debug register 7, the debug control.</summary>
    public uint Dr7 { get; set; }

    /// <summary>Whether the processor has stopped at a <c>HLT</c>. <see cref="Step"/> does nothing while it has.</summary>
    public bool Halted { get; set; }

    /// <summary>The vector of the last fault an instruction raised, for debugging; -1 before any.</summary>
    internal int LastFault { get; private set; } = -1;

    /// <summary>
    /// Code outside the machine that the bytes <c>0F FF</c> and a service number call, given the
    /// number. No x86 has an instruction there, so programs never use them; the emulator puts
    /// them in its BIOS and DOS to answer interrupts in C#. With no handler set they're an invalid
    /// opcode, as on the chip.
    /// </summary>
    public Action<byte>? HostCall { get; set; }

    /// <summary>The number of instructions run, faulting ones and repeated string instructions counted once.</summary>
    public long Instructions { get; private set; }

    /// <summary>
    /// The times repeated string instructions (<c>REP MOVS</c> and the rest) have repeated, each
    /// a step of the instruction's own. A machine counts them into its time, as DOSBox does: a
    /// <c>REP MOVSD</c> copying a screen is tens of thousands of steps on the chip, not one.
    /// </summary>
    internal long Repetitions { get; private set; }

    /// <summary>
    /// How many times a repeated string instruction repeats in one <see cref="Step"/> before it
    /// lets the next instruction step, interrupts included, come first; 0 for no limit. The chip
    /// takes interrupts between repetitions in the same way: it leaves the instruction pointer
    /// on the instruction, with CX counting what's left, and carries on after the handler.
    /// </summary>
    internal int RepeatLimit { get; set; }

    /// <summary>
    /// Runs one instruction. When it faults, the processor is left at the start of the fault's
    /// handler instead.
    /// </summary>
    public void Step()
    {
        if (Halted)
        {
            return;
        }

        try
        {
            Begin();
            Execute();
        }
        catch (CpuFault fault)
        {
            Faulted(fault);
        }
    }

    /// <summary>
    /// Set by the machine when the program has done something its devices must see to: used a
    /// port, or called a service. <see cref="RunFor"/> stops after the instruction.
    /// </summary>
    internal bool Stopping { get; set; }

    /// <summary>Set by the machine while an interrupt waits at its controller, for <see cref="RunFor"/> to stop for once interrupts are on.</summary>
    internal bool InterruptWaiting { get; set; }

    /// <summary>
    /// Runs instructions for the given number of steps, a step being an instruction or a
    /// repetition of a string instruction; or less, if one is <see cref="Stopping"/>, the
    /// processor halts, or interrupts are on with one <see cref="InterruptWaiting"/>. It's
    /// <see cref="Step"/> over and over, without the cost of entering it for each instruction.
    /// </summary>
    /// <returns>The steps taken.</returns>
    internal long RunFor(long steps)
    {
        long start = Instructions + Repetitions;
        Stopping = false;
        while (!Halted)
        {
            try
            {
                do
                {
                    Begin();
                    Execute();
                }
                while (Instructions + Repetitions - start < steps && !Stopping && !Halted && !(InterruptWaiting && InterruptsEnabled));

                break;
            }
            catch (CpuFault fault)
            {
                Faulted(fault);
                if (Instructions + Repetitions - start >= steps || Stopping)
                {
                    break;
                }
            }
        }

        return Instructions + Repetitions - start;
    }

    /// <summary>The start of an instruction: where it is, and the prefixes' defaults.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void Begin()
    {
        _start = _eip;
        _startEsp = _regs[ESP];
        _length = 0;

        // a 32-bit code segment's instructions default to 32-bit operands and addresses, and the
        // prefixes switch them to 16
        _operand32 = _big[CS];
        _address32 = _operand32;
        _segment = -1;
        _repeat = Repeat.None;
        Instructions++;
    }

    /// <summary>
    /// An instruction faulted. A fault is restartable: the handler gets the address of the
    /// instruction, prefixes included, and the stack as the instruction found it.
    /// </summary>
    private void Faulted(CpuFault fault)
    {
        _eip = _start;
        _regs[ESP] = _startEsp;
        LastFault = fault.Vector;
        RaiseFault(fault);
    }

    /// <summary>
    /// Enters a fault's handler. A fault while entering it is a double fault, and a fault while
    /// entering that would shut the processor down: the emulator stops there instead.
    /// </summary>
    private void RaiseFault(CpuFault fault)
    {
        try
        {
            EnterInterrupt(fault.Vector, _start, fault.ErrorCode, External.No);
        }
        catch (CpuFault second) when (fault.Vector != Vector.DoubleFault)
        {
            _regs[ESP] = _startEsp;
            LastFault = Vector.DoubleFault;
            try
            {
                EnterInterrupt(Vector.DoubleFault, _start, 0, External.No);
            }
            catch (CpuFault)
            {
                throw new NotSupportedException(
                    $"The processor shut down: fault {second.Vector} entering the handler of fault {fault.Vector}, and another entering the double fault's.");
            }
        }
    }

    /// <summary>Whether the interrupt flag is set, so that hardware interrupts are taken.</summary>
    public bool InterruptsEnabled => (_flags & Flag.Interrupt) != 0;

    /// <summary>
    /// Takes a hardware interrupt between instructions, as the interrupt controller delivers it,
    /// waking the processor from a <c>HLT</c>. The caller checks <see cref="InterruptsEnabled"/>.
    /// </summary>
    public void Interrupt(int vector)
    {
        Halted = false;
        _start = _eip;
        _startEsp = _regs[ESP];
        try
        {
            EnterInterrupt(vector, _eip, -1, External.Yes);
        }
        catch (CpuFault fault)
        {
            // a bad gate or handler: its fault is raised instead, as for an instruction
            _regs[ESP] = _startEsp;
            LastFault = fault.Vector;
            RaiseFault(fault);
        }
    }

    /// <summary>
    /// Loads a segment register. In real mode only the selector and base change: the rest of the
    /// descriptor cache stays as it was, which is how a program that set a large limit in
    /// protected mode keeps it back in real mode. In protected mode the selector picks a descriptor.
    /// </summary>
    private void LoadSegment(int segment, ushort selector)
    {
        if (ProtectedMode)
        {
            LoadProtectedSegment(segment, selector);
            return;
        }

        _selectors[segment] = selector;
        _bases[segment] = (uint)selector << 4;
    }

    /// <summary>Where an interrupt came from: the processor itself, the interrupt controller, or a program.</summary>
    private enum External : byte
    {
        No,
        Yes,

        // INT n, INT 3 and INTO: in protected mode their gates must allow the program's privilege
        Software,
    }

    /// <summary>
    /// Enters an interrupt or exception handler: the real-mode way, or through the protected-mode
    /// interrupt table. An error code of -1 is none.
    /// </summary>
    private void EnterInterrupt(int vector, uint returnIp, int errorCode = -1, External source = External.Software)
    {
        if (ProtectedMode)
        {
            EnterProtectedInterrupt(vector, returnIp, errorCode, source);
            return;
        }

        EnterRealInterrupt(vector, returnIp);
    }

    /// <summary>
    /// Enters an interrupt or exception handler the real-mode way: the flags, CS and IP go on the
    /// stack as words, interrupts and single-stepping are turned off, and the vector's entry in the
    /// interrupt table (at address 0, unless LIDT moved it) gives the handler's CS:IP.
    /// </summary>
    /// <remarks>
    /// The vector is read before anything is pushed. It matters when the stack overlaps the table:
    /// the pushes may overwrite the very entry being used, and the chip still goes where the entry
    /// pointed beforehand.
    /// </remarks>
    private void EnterRealInterrupt(int vector, uint returnIp)
    {
        uint entry = _idtBase + ((uint)vector * 4);
        uint ip = (uint)(_bus.Read(entry) | (_bus.Read(entry + 1) << 8));
        ushort cs = (ushort)(_bus.Read(entry + 2) | (_bus.Read(entry + 3) << 8));

        Push((ushort)_flags, 2);
        Push(_selectors[CS], 2);
        Push((ushort)returnIp, 2);
        _flags &= ~(Flag.Interrupt | Flag.Trap);

        LoadSegment(CS, cs);
        _eip = ip;
    }

    // the exceptions the processor raises
    private static class Vector
    {
        public const int DivideError = 0;
        public const int Debug = 1;
        public const int Breakpoint = 3;
        public const int Overflow = 4;
        public const int Bound = 5;
        public const int InvalidOpcode = 6;
        public const int DoubleFault = 8;
        public const int InvalidTss = 10;
        public const int SegmentNotPresent = 11;
        public const int StackFault = 12;
        public const int GeneralProtection = 13;
    }

    /// <summary>
    /// A fault. In protected mode the double fault and the faults from 10 to 14 push an error
    /// code, most often the selector at fault; in real mode none does.
    /// </summary>
    private CpuFault Fault(int vector, int errorCode = 0)
        => new(vector, ProtectedMode && vector is Vector.DoubleFault or (>= Vector.InvalidTss and <= 14) ? errorCode : -1);

    private static CpuFault InvalidOpcode() => new(Vector.InvalidOpcode, -1);

    /// <summary>
    /// A fault, thrown from wherever in an instruction it's found and caught by <see cref="Step"/>.
    /// Faults are rare in the programs this core runs, so the cost of an exception doesn't matter,
    /// and throwing keeps every memory access free of error checks along the way.
    /// </summary>
    /// <param name="vector">The exception.</param>
    /// <param name="errorCode">The error code pushed for it; -1 for none.</param>
    private sealed class CpuFault(int vector, int errorCode) : Exception
    {
        public int Vector { get; } = vector;

        public int ErrorCode { get; } = errorCode;
    }
}
