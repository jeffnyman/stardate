namespace Stardate.Core.Processors;

// Protected mode: descriptor tables, segment loads, privilege, far transfers, interrupts through
// gates, and the system instructions. After DOSBox's cpu.cpp.
public sealed partial class Intel386
{
    // the descriptor tables: the global and interrupt tables by linear base and limit, the local
    // table and the task state segment by selector, with the base and limit their descriptors gave
    private uint _gdtBase, _gdtLimit = 0xFFFF;
    private uint _idtBase, _idtLimit = 0x3FF;
    private ushort _ldtSelector;
    private uint _ldtBase, _ldtLimit;
    private ushort _taskSelector;
    private uint _taskBase, _taskLimit;
    private byte _taskType;

    // the current privilege level: 0, the most privileged, to 3; always 0 in real mode
    private int _cpl;

    // the debug and test registers' values, kept for programs that read back what they wrote
    private readonly uint[] _debug = new uint[8];
    private readonly uint[] _test = new uint[8];

    /// <summary>Whether the processor is in protected mode: CR0's PE bit.</summary>
    public bool ProtectedMode => (Cr0 & 1) != 0;

    /// <summary>The current privilege level, from 0 (the most privileged) to 3.</summary>
    internal int Cpl => _cpl;

    /// <summary>Control register 2, the address of the last page fault.</summary>
    internal uint Cr2 { get; set; }

    /// <summary>The global descriptor table's linear address and limit.</summary>
    internal (uint Base, uint Limit) Gdt
    {
        get => (_gdtBase, _gdtLimit);
        set => (_gdtBase, _gdtLimit) = value;
    }

    /// <summary>The interrupt descriptor table's linear address and limit; in real mode, the interrupt vector table's.</summary>
    internal (uint Base, uint Limit) Idt
    {
        get => (_idtBase, _idtLimit);
        set => (_idtBase, _idtLimit) = value;
    }

    // the flags only protected mode gives meaning to
    private const uint IoplMask = 0x3000;
    private const uint NestedTask = 0x4000;
    private const uint Resume = 0x1_0000;
    private const uint Virtual8086 = 0x2_0000;

    private int Iopl => (int)((_flags & IoplMask) >> 12);

    /// <summary>A segment descriptor or gate: eight bytes in a descriptor table, as two dwords.</summary>
    private readonly struct Descriptor(uint low, uint high)
    {
        public uint Low => low;

        public uint High => high;

        public uint Base => (low >> 16) | ((high & 0xFF) << 16) | (high & 0xFF00_0000);

        /// <summary>The limit, in bytes: with the granularity bit set it counts 4 KB pages.</summary>
        public uint Limit
        {
            get
            {
                uint raw = (low & 0xFFFF) | (high & 0xF_0000);
                return (high & 0x80_0000) != 0 ? (raw << 12) | 0xFFF : raw;
            }
        }

        public byte Access => (byte)(high >> 8);

        public bool Present => (high & 0x8000) != 0;

        public int Dpl => (int)((high >> 13) & 3);

        /// <summary>A code or data segment, rather than a system descriptor or a gate.</summary>
        public bool IsSegment => (high & 0x1000) != 0;

        public bool IsCode => IsSegment && (high & 0x800) != 0;

        public bool IsData => IsSegment && (high & 0x800) == 0;

        /// <summary>For code: callable from less privileged code, running at the caller's privilege.</summary>
        public bool Conforming => (high & 0x400) != 0;

        /// <summary>For data: a stack's segment, whose valid offsets lie above the limit.</summary>
        public bool ExpandDown => (high & 0x400) != 0;

        /// <summary>For code: readable as well as executable.</summary>
        public bool Readable => (high & 0x200) != 0;

        /// <summary>For data: writable as well as readable.</summary>
        public bool Writable => (high & 0x200) != 0;

        /// <summary>The type of a system descriptor or gate.</summary>
        public int SystemType => (int)((high >> 8) & 0xF);

        /// <summary>The D/B bit: 32-bit code, or a stack addressed by ESP.</summary>
        public bool Big => (high & 0x40_0000) != 0;

        public ushort GateSelector => (ushort)(low >> 16);

        public uint GateOffset => (low & 0xFFFF) | (high & 0xFFFF_0000);
    }

    // the system descriptors' types
    private static class SystemType
    {
        public const int AvailableTss16 = 1;
        public const int Ldt = 2;
        public const int BusyTss16 = 3;
        public const int CallGate16 = 4;
        public const int TaskGate = 5;
        public const int InterruptGate16 = 6;
        public const int TrapGate16 = 7;
        public const int AvailableTss32 = 9;
        public const int BusyTss32 = 11;
        public const int CallGate32 = 12;
        public const int InterruptGate32 = 14;
        public const int TrapGate32 = 15;
    }

    // ---- descriptors -------------------------------------------------------------------------

    private uint ReadLinear32(uint address)
        => _bus.Read(address) | ((uint)_bus.Read(address + 1) << 8) | ((uint)_bus.Read(address + 2) << 16) | ((uint)_bus.Read(address + 3) << 24);

    /// <summary>
    /// Whether a selector's descriptor lies within its table (the local one when its TI bit is
    /// set), and if so the descriptor and where it is.
    /// </summary>
    private bool TryReadDescriptor(ushort selector, out Descriptor descriptor, out uint address)
    {
        uint index = (uint)selector & 0xFFF8;
        uint tableBase, tableLimit;
        if ((selector & 4) != 0)
        {
            if ((_ldtSelector & 0xFFFC) == 0)
            {
                descriptor = default;
                address = 0;
                return false;
            }

            (tableBase, tableLimit) = (_ldtBase, _ldtLimit);
        }
        else
        {
            (tableBase, tableLimit) = (_gdtBase, _gdtLimit);
        }

        if (index + 7 > tableLimit)
        {
            descriptor = default;
            address = 0;
            return false;
        }

        address = tableBase + index;
        descriptor = new Descriptor(ReadLinear32(address), ReadLinear32(address + 4));
        return true;
    }

    /// <summary>A selector's descriptor; a general protection fault naming the selector if it's outside its table.</summary>
    private Descriptor ReadDescriptor(ushort selector, int vector = Vector.GeneralProtection, int external = 0)
    {
        if (!TryReadDescriptor(selector, out var descriptor, out uint address))
        {
            throw Fault(vector, (selector & 0xFFFC) | external);
        }

        // a segment's descriptor is marked accessed when it's loaded
        if (descriptor.IsSegment && (descriptor.Access & 1) == 0)
        {
            _bus.Write(address + 5, (byte)(descriptor.Access | 1));
        }

        return descriptor;
    }

    /// <summary>Fills a segment register's descriptor cache from a descriptor.</summary>
    private void LoadCache(int segment, ushort selector, Descriptor descriptor)
    {
        _selectors[segment] = selector;
        _bases[segment] = descriptor.Base;
        _limits[segment] = descriptor.Limit;
        _access[segment] = (byte)(descriptor.Access | 1);
        _big[segment] = descriptor.Big;
        _use[segment] = descriptor.IsCode
            ? (descriptor.Readable ? SegmentUse.Read : SegmentUse.None)
            : SegmentUse.Read
                | (descriptor.Writable ? SegmentUse.Write : SegmentUse.None)
                | (descriptor.ExpandDown ? SegmentUse.ExpandDown : SegmentUse.None);
    }

    /// <summary>Loads CS from a code segment's descriptor; the selector's RPL becomes the privilege level.</summary>
    private void LoadCode(ushort selector, Descriptor descriptor)
    {
        LoadCache(CS, selector, descriptor);
        _cpl = selector & 3;
    }

    /// <summary>
    /// Loads a data segment register or SS in protected mode, with the processor's checks. A null
    /// selector may go in DS, ES, FS or GS, leaving the register unusable until it's loaded again;
    /// SS can't take one.
    /// </summary>
    private void LoadProtectedSegment(int segment, ushort selector)
    {
        int index = selector & 0xFFFC;
        int rpl = selector & 3;

        if (segment == CS)
        {
            // only the emulator's own services load CS directly: a code segment, no questions
            var code = ReadDescriptor(selector);
            if (!code.IsCode)
            {
                throw Fault(Vector.GeneralProtection, index);
            }

            LoadCode(selector, code);
            return;
        }

        if (index == 0)
        {
            if (segment == SS)
            {
                throw Fault(Vector.GeneralProtection, 0);
            }

            _selectors[segment] = selector;
            _bases[segment] = 0;
            _limits[segment] = 0;
            _use[segment] = SegmentUse.None;
            return;
        }

        if (segment == SS)
        {
            // the stack: writable data, at exactly the current privilege
            var stack = ReadDescriptor(selector);
            if (rpl != _cpl || !stack.IsData || !stack.Writable || stack.Dpl != _cpl)
            {
                throw Fault(Vector.GeneralProtection, index);
            }

            if (!stack.Present)
            {
                throw Fault(Vector.StackFault, index);
            }

            LoadCache(SS, selector, stack);
            return;
        }

        // data, or code that can be read; and unless it's conforming code, no more privileged
        // than both the program and the selector
        var data = ReadDescriptor(selector);
        if (!(data.IsData || (data.IsCode && data.Readable)))
        {
            throw Fault(Vector.GeneralProtection, index);
        }

        if ((data.IsData || !data.Conforming) && (data.Dpl < _cpl || data.Dpl < rpl))
        {
            throw Fault(Vector.GeneralProtection, index);
        }

        if (!data.Present)
        {
            throw Fault(Vector.SegmentNotPresent, index);
        }

        LoadCache(segment, selector, data);
    }

    /// <summary>
    /// After a return to a less privileged level, the data segment registers that hold segments
    /// more privileged than it are made null, so the outer code can't keep using them.
    /// </summary>
    private void NullMorePrivilegedSegments()
    {
        foreach (int segment in (ReadOnlySpan<int>)[ES, DS, FS, GS])
        {
            byte access = _access[segment];
            bool conformingCode = (access & 0x0C) == 0x0C;
            if (_use[segment] != SegmentUse.None && !conformingCode && ((access >> 5) & 3) < _cpl)
            {
                _selectors[segment] = 0;
                _use[segment] = SegmentUse.None;
            }
        }
    }

    // ---- far transfers -----------------------------------------------------------------------

    /// <summary>A far jump or call's target in protected mode: a code segment at the right privilege.</summary>
    private void ProtectedJump(ushort selector, uint offset)
    {
        int index = selector & 0xFFFC;
        if (index == 0)
        {
            throw Fault(Vector.GeneralProtection, 0);
        }

        var target = ReadDescriptor(selector);
        if (!target.IsSegment)
        {
            throw target.SystemType switch
            {
                SystemType.CallGate16 or SystemType.CallGate32 => new NotSupportedException("A far jump or call through a call gate isn't emulated yet."),
                SystemType.TaskGate or SystemType.AvailableTss16 or SystemType.AvailableTss32 => new NotSupportedException("A task switch isn't emulated yet."),
                _ => Fault(Vector.GeneralProtection, index),
            };
        }

        // a conforming segment runs at the caller's privilege, if it's no more privileged than
        // that; any other only at its own, which must be the caller's
        bool allowed = target.IsCode && (target.Conforming
            ? target.Dpl <= _cpl
            : (selector & 3) <= _cpl && target.Dpl == _cpl);
        if (!allowed)
        {
            throw Fault(Vector.GeneralProtection, index);
        }

        if (!target.Present)
        {
            throw Fault(Vector.SegmentNotPresent, index);
        }

        if (offset > target.Limit)
        {
            throw Fault(Vector.GeneralProtection, 0);
        }

        LoadCode((ushort)(index | _cpl), target);
        _eip = offset;
    }

    /// <summary>
    /// The code segment a far return or IRET goes back to, checked: no more privileged than the
    /// code returning (its RPL is the privilege returned to), and holding the offset.
    /// </summary>
    private Descriptor ReturnTarget(ushort selector, uint offset)
    {
        int index = selector & 0xFFFC;
        int rpl = selector & 3;
        if (index == 0)
        {
            throw Fault(Vector.GeneralProtection, 0);
        }

        if (rpl < _cpl)
        {
            throw Fault(Vector.GeneralProtection, index);
        }

        var target = ReadDescriptor(selector);
        if (!target.IsCode || (target.Conforming ? target.Dpl > rpl : target.Dpl != rpl))
        {
            throw Fault(Vector.GeneralProtection, index);
        }

        if (!target.Present)
        {
            throw Fault(Vector.SegmentNotPresent, index);
        }

        if (offset > target.Limit)
        {
            throw Fault(Vector.GeneralProtection, 0);
        }

        return target;
    }

    /// <summary>The stack a return to an outer privilege level goes back to, checked.</summary>
    private Descriptor OuterStack(ushort selector, int rpl)
    {
        int index = selector & 0xFFFC;
        if (index == 0)
        {
            throw Fault(Vector.GeneralProtection, 0);
        }

        var stack = ReadDescriptor(selector);
        if ((selector & 3) != rpl || !stack.IsData || !stack.Writable || stack.Dpl != rpl)
        {
            throw Fault(Vector.GeneralProtection, index);
        }

        if (!stack.Present)
        {
            throw Fault(Vector.StackFault, index);
        }

        return stack;
    }

    /// <summary>
    /// RETF in protected mode, releasing <paramref name="release"/> bytes of parameters. A return
    /// to a less privileged level also takes the caller's stack, which follows the parameters.
    /// </summary>
    private void ProtectedReturn(int size, ushort release)
    {
        uint offset = PeekStack(0, size);
        ushort selector = (ushort)PeekStack(size, size);
        if (size == 2)
        {
            offset &= 0xFFFF;
        }

        int rpl = selector & 3;
        var code = ReturnTarget(selector, offset);
        if (rpl == _cpl)
        {
            SetSp(_regs[ESP] + (uint)(2 * size) + release);
            LoadCode(selector, code);
            _eip = offset;
            return;
        }

        uint esp = PeekStack((2 * size) + release, size);
        ushort ss = (ushort)PeekStack((3 * size) + release, size);
        var stack = OuterStack(ss, rpl);

        LoadCode(selector, code);
        LoadCache(SS, ss, stack);
        _regs[ESP] = size == 4 ? esp : (_regs[ESP] & 0xFFFF_0000) | (esp & 0xFFFF);
        SetSp(_regs[ESP] + release);
        _eip = offset;
        NullMorePrivilegedSegments();
    }

    /// <summary>IRET in protected mode, to the same privilege level or a less privileged one.</summary>
    private void ProtectedInterruptReturn(int size)
    {
        if ((_flags & NestedTask) != 0)
        {
            throw new NotSupportedException("A return from a nested task isn't emulated yet.");
        }

        uint offset = PeekStack(0, size);
        ushort selector = (ushort)PeekStack(size, size);
        uint flags = PeekStack(2 * size, size);
        if (size == 4 && (flags & Virtual8086) != 0 && _cpl == 0)
        {
            throw new NotSupportedException("A return to virtual-8086 mode isn't emulated yet.");
        }

        if (size == 2)
        {
            offset &= 0xFFFF;
        }

        int rpl = selector & 3;
        var code = ReturnTarget(selector, offset);
        if (rpl == _cpl)
        {
            SetSp(_regs[ESP] + (uint)(3 * size));
            LoadCode(selector, code);
            _eip = offset;
            SetFlags(flags, size);
            return;
        }

        uint esp = PeekStack(3 * size, size);
        ushort ss = (ushort)PeekStack(4 * size, size);
        var stack = OuterStack(ss, rpl);

        // the flags as the more privileged code may set them, before the privilege drops
        SetFlags(flags, size);
        LoadCode(selector, code);
        LoadCache(SS, ss, stack);
        _regs[ESP] = size == 4 ? esp : (_regs[ESP] & 0xFFFF_0000) | (esp & 0xFFFF);
        _eip = offset;
        NullMorePrivilegedSegments();
    }

    // ---- interrupts --------------------------------------------------------------------------

    /// <summary>
    /// Enters an interrupt or exception handler through the interrupt descriptor table: an
    /// interrupt gate (which also turns interrupts off) or a trap gate, to a code segment. A
    /// handler more privileged than the code interrupted runs on that level's own stack, from the
    /// task state segment, with the old stack's SS:ESP pushed on the new one first.
    /// </summary>
    private void EnterProtectedInterrupt(int vector, uint returnIp, int errorCode, External source)
    {
        int external = source == External.Yes ? 1 : 0;
        int gateCode = (vector * 8) + 2 + external;
        uint at = (uint)vector * 8;
        if (at + 7 > _idtLimit)
        {
            throw Fault(Vector.GeneralProtection, gateCode);
        }

        var gate = new Descriptor(ReadLinear32(_idtBase + at), ReadLinear32(_idtBase + at + 4));
        int type = gate.SystemType;
        if (gate.IsSegment || type is not (SystemType.InterruptGate16 or SystemType.TrapGate16 or SystemType.InterruptGate32 or SystemType.TrapGate32))
        {
            if (!gate.IsSegment && type == SystemType.TaskGate)
            {
                throw new NotSupportedException($"Interrupt {vector:X2}h through a task gate isn't emulated yet.");
            }

            throw Fault(Vector.GeneralProtection, gateCode);
        }

        // a program's INT can use only the gates its privilege may
        if (source == External.Software && gate.Dpl < _cpl)
        {
            throw Fault(Vector.GeneralProtection, gateCode);
        }

        if (!gate.Present)
        {
            throw Fault(Vector.SegmentNotPresent, gateCode);
        }

        EnterGate(gate, returnIp, errorCode, external);
    }

    /// <summary>
    /// Goes through an interrupt or trap gate to its handler, once the gate itself has passed its
    /// checks: the handler's code segment checked, the stack switched if the handler is more
    /// privileged, and the return frame pushed.
    /// </summary>
    private void EnterGate(Descriptor gate, uint returnIp, int errorCode, int external)
    {
        int type = gate.SystemType;
        ushort selector = gate.GateSelector;
        int index = selector & 0xFFFC;
        if (index == 0)
        {
            throw Fault(Vector.GeneralProtection, external);
        }

        var code = ReadDescriptor(selector, Vector.GeneralProtection, external);
        if (!code.IsCode || code.Dpl > _cpl)
        {
            throw Fault(Vector.GeneralProtection, index | external);
        }

        if (!code.Present)
        {
            throw Fault(Vector.SegmentNotPresent, index | external);
        }

        bool gate32 = type >= SystemType.InterruptGate32;
        int size = gate32 ? 4 : 2;
        uint offset = gate32 ? gate.GateOffset : gate.GateOffset & 0xFFFF;
        if (offset > code.Limit)
        {
            throw Fault(Vector.GeneralProtection, 0);
        }

        int newCpl = code.Conforming ? _cpl : code.Dpl;
        uint flags = _flags;
        ushort oldCs = _selectors[CS];
        var savedStack = SaveCache(SS);
        uint savedEsp = _regs[ESP];
        int savedCpl = _cpl;

        try
        {
            if (newCpl < _cpl)
            {
                // the inner level's stack, from the task state segment
                (ushort ss, uint esp) = TaskStack(newCpl, external);
                int ssIndex = ss & 0xFFFC;
                if (ssIndex == 0)
                {
                    throw Fault(Vector.InvalidTss, external);
                }

                var stack = ReadDescriptor(ss, Vector.InvalidTss, external);
                if ((ss & 3) != newCpl || stack.Dpl != newCpl || !stack.IsData || !stack.Writable)
                {
                    throw Fault(Vector.InvalidTss, ssIndex | external);
                }

                if (!stack.Present)
                {
                    throw Fault(Vector.StackFault, ssIndex | external);
                }

                ushort oldSs = _selectors[SS];
                uint oldEsp = _regs[ESP];
                LoadCache(SS, ss, stack);
                _regs[ESP] = stack.Big ? esp : (_regs[ESP] & 0xFFFF_0000) | (esp & 0xFFFF);
                Push(oldSs, size);
                Push(oldEsp, size);
            }

            Push(flags, size);
            Push(oldCs, size);
            Push(returnIp, size);
            if (errorCode >= 0)
            {
                Push((uint)errorCode, size);
            }
        }
        catch (CpuFault)
        {
            // the old stack back, for the fault's own handler
            RestoreCache(SS, savedStack);
            _regs[ESP] = savedEsp;
            _cpl = savedCpl;
            throw;
        }

        _flags &= ~(Flag.Trap | NestedTask | Resume | Virtual8086);
        if (type is SystemType.InterruptGate16 or SystemType.InterruptGate32)
        {
            _flags &= ~Flag.Interrupt;
        }

        LoadCode((ushort)(index | newCpl), code);
        _eip = offset;
    }

    /// <summary>A privilege level's stack, SS:ESP, from the current task state segment.</summary>
    private (ushort Ss, uint Esp) TaskStack(int level, int external)
    {
        bool tss32 = _taskType is SystemType.AvailableTss32 or SystemType.BusyTss32;
        bool tss16 = _taskType is SystemType.AvailableTss16 or SystemType.BusyTss16;
        uint at = tss32 ? (uint)(4 + (8 * level)) : (uint)(2 + (4 * level));
        uint needed = tss32 ? at + 7 : at + 3;
        if ((!tss32 && !tss16) || needed > _taskLimit)
        {
            throw Fault(Vector.InvalidTss, (_taskSelector & 0xFFFC) | external);
        }

        uint address = _taskBase + at;
        return tss32
            ? ((ushort)ReadLinear32(address + 4), ReadLinear32(address))
            : ((ushort)(_bus.Read(address + 2) | (_bus.Read(address + 3) << 8)), (uint)(_bus.Read(address) | (_bus.Read(address + 1) << 8)));
    }

    internal readonly record struct SegmentCache(ushort Selector, uint Base, uint Limit, byte Access, bool Big, SegmentUse Use);

    private SegmentCache SaveCache(int segment)
        => new(_selectors[segment], _bases[segment], _limits[segment], _access[segment], _big[segment], _use[segment]);

    private void RestoreCache(int segment, SegmentCache cache)
    {
        _selectors[segment] = cache.Selector;
        _bases[segment] = cache.Base;
        _limits[segment] = cache.Limit;
        _access[segment] = cache.Access;
        _big[segment] = cache.Big;
        _use[segment] = cache.Use;
    }

    // ---- privilege checks --------------------------------------------------------------------

    /// <summary>The instructions only the most privileged code may run: a general protection fault at any other level.</summary>
    private void RequirePrivilege()
    {
        if (ProtectedMode && _cpl != 0)
        {
            throw Fault(Vector.GeneralProtection, 0);
        }
    }

    /// <summary>CLI and STI: allowed at a privilege level no less privileged than the I/O privilege level.</summary>
    private void RequireIoPrivilege()
    {
        if (ProtectedMode && _cpl > Iopl)
        {
            throw Fault(Vector.GeneralProtection, 0);
        }
    }

    /// <summary>
    /// IN and OUT, and INS and OUTS: allowed at the I/O privilege level, or for ports the task
    /// state segment's I/O permission bitmap leaves open.
    /// </summary>
    private void CheckPort(ushort port, int size)
    {
        if (!ProtectedMode || _cpl <= Iopl)
        {
            return;
        }

        bool tss32 = _taskType is SystemType.AvailableTss32 or SystemType.BusyTss32;
        if (!tss32 || _taskLimit < 0x67)
        {
            throw Fault(Vector.GeneralProtection, 0);
        }

        uint bitmap = (uint)(_bus.Read(_taskBase + 0x66) | (_bus.Read(_taskBase + 0x67) << 8));
        for (int i = 0; i < size; i++)
        {
            uint bit = (uint)(port + i);
            uint at = bitmap + (bit / 8);
            if (at > _taskLimit || (_bus.Read(_taskBase + at) & (1 << (int)(bit % 8))) != 0)
            {
                throw Fault(Vector.GeneralProtection, 0);
            }
        }
    }

    // ---- the system instructions -------------------------------------------------------------

    /// <summary>Switches between real and protected mode as CR0's PE bit changes; paging isn't emulated.</summary>
    private void WriteCr0(uint value)
    {
        if ((value & 0x8000_0000) != 0)
        {
            throw new NotSupportedException("Paging isn't emulated yet.");
        }

        // the 386 always reports its extension type bit set: a 387-style coprocessor interface
        Cr0 = value | 0x10;
        _cpl = 0;
    }

    /// <summary>0F 00: SLDT, STR, LLDT, LTR, VERR and VERW, all protected mode's alone.</summary>
    private void DescriptorTableGroup()
    {
        if (!ProtectedMode)
        {
            throw InvalidOpcode();
        }

        DecodeModRm();
        switch (_reg)
        {
            case 0: WriteRm(_mod == 3 ? OperandSize : 2, _ldtSelector); return;
            case 1: WriteRm(_mod == 3 ? OperandSize : 2, _taskSelector); return;

            case 2:
            {
                RequirePrivilege();
                ushort selector = (ushort)ReadRm(2);
                LoadLdt(selector);
                return;
            }

            case 3:
            {
                RequirePrivilege();
                ushort selector = (ushort)ReadRm(2);
                LoadTaskRegister(selector);
                return;
            }

            case 4:
            case 5:
            {
                // VERR and VERW: whether the segment could be read, or written, at this privilege
                ushort selector = (ushort)ReadRm(2);
                bool ok = (selector & 0xFFFC) != 0
                    && TryReadDescriptor(selector, out var descriptor, out _)
                    && descriptor.IsSegment
                    && (_reg == 4 ? descriptor.IsData || descriptor.Readable : descriptor.IsData && descriptor.Writable)
                    && ((descriptor.IsCode && descriptor.Conforming) || (descriptor.Dpl >= _cpl && descriptor.Dpl >= (selector & 3)));
                Set(Flag.Zero, ok);
                return;
            }

            default: throw InvalidOpcode();
        }
    }

    private void LoadLdt(ushort selector)
    {
        int index = selector & 0xFFFC;
        if (index == 0)
        {
            _ldtSelector = selector;
            _ldtBase = 0;
            _ldtLimit = 0;
            return;
        }

        // a local table's descriptor is always in the global table
        if ((selector & 4) != 0)
        {
            throw Fault(Vector.GeneralProtection, index);
        }

        var table = ReadDescriptor(selector);
        if (table.IsSegment || table.SystemType != SystemType.Ldt)
        {
            throw Fault(Vector.GeneralProtection, index);
        }

        if (!table.Present)
        {
            throw Fault(Vector.SegmentNotPresent, index);
        }

        _ldtSelector = selector;
        _ldtBase = table.Base;
        _ldtLimit = table.Limit;
    }

    private void LoadTaskRegister(ushort selector)
    {
        int index = selector & 0xFFFC;
        if (index == 0)
        {
            throw Fault(Vector.GeneralProtection, 0);
        }

        if ((selector & 4) != 0 || !TryReadDescriptor(selector, out var task, out uint address))
        {
            throw Fault(Vector.GeneralProtection, index);
        }

        if (task.IsSegment || task.SystemType is not (SystemType.AvailableTss16 or SystemType.AvailableTss32))
        {
            throw Fault(Vector.GeneralProtection, index);
        }

        if (!task.Present)
        {
            throw Fault(Vector.SegmentNotPresent, index);
        }

        // the task is now busy
        _bus.Write(address + 5, (byte)(task.Access | 2));
        _taskSelector = selector;
        _taskBase = task.Base;
        _taskLimit = task.Limit;
        _taskType = (byte)(task.SystemType | 2);
    }

    /// <summary>0F 01: SGDT, SIDT, LGDT, LIDT, SMSW and LMSW.</summary>
    private void SystemTableGroup()
    {
        DecodeModRm();
        switch (_reg)
        {
            case 0:
            case 1:
            {
                // SGDT and SIDT: the limit, then the base. The 386 stores all of the base,
                // whatever the operand size, as DOSBox does
                if (_mod == 3)
                {
                    throw InvalidOpcode();
                }

                (uint tableBase, uint limit) = _reg == 0 ? (_gdtBase, _gdtLimit) : (_idtBase, _idtLimit);
                WriteMemory(_eaSegment, _eaOffset, 2, limit);
                WriteMemory(_eaSegment, NextOffset(_eaOffset, 2), 4, tableBase);
                return;
            }

            case 2:
            case 3:
            {
                // LGDT and LIDT: with a 16-bit operand, only 24 bits of the base
                if (_mod == 3)
                {
                    throw InvalidOpcode();
                }

                RequirePrivilege();
                uint limit = ReadMemory(_eaSegment, _eaOffset, 2);
                uint tableBase = ReadMemory(_eaSegment, NextOffset(_eaOffset, 2), 4);
                if (!_operand32)
                {
                    tableBase &= 0xFF_FFFF;
                }

                if (_reg == 2)
                {
                    (_gdtBase, _gdtLimit) = (tableBase, limit);
                }
                else
                {
                    (_idtBase, _idtLimit) = (tableBase, limit);
                }

                return;
            }

            // SMSW: CR0's low word, the 286's machine status word
            case 4: WriteRm(_mod == 3 ? OperandSize : 2, Cr0 & (_mod == 3 && _operand32 ? 0xFFFF_FFFF : 0xFFFF)); return;

            case 6:
            {
                // LMSW: CR0's low four bits; it can set PE, but not clear it
                RequirePrivilege();
                uint value = ReadRm(2);
                WriteCr0((Cr0 & ~0xFu) | (value & 0xF) | (Cr0 & 1));
                return;
            }

            default: throw InvalidOpcode();
        }
    }

    /// <summary>0F 02 and 0F 03: LAR and LSL, a descriptor's access rights or limit, if this privilege may see it.</summary>
    private void LoadAccessOrLimit(bool limit)
    {
        if (!ProtectedMode)
        {
            throw InvalidOpcode();
        }

        int size = OperandSize;
        DecodeModRm();
        ushort selector = (ushort)ReadRm(2);
        if ((selector & 0xFFFC) == 0 || !TryReadDescriptor(selector, out var descriptor, out _))
        {
            Set(Flag.Zero, false);
            return;
        }

        bool validType = descriptor.IsSegment || (limit
            ? descriptor.SystemType is SystemType.AvailableTss16 or SystemType.Ldt or SystemType.BusyTss16 or SystemType.AvailableTss32 or SystemType.BusyTss32
            : descriptor.SystemType is not (0 or 8 or 10 or 13 or SystemType.InterruptGate16 or SystemType.TrapGate16 or SystemType.InterruptGate32 or SystemType.TrapGate32));
        bool visible = (descriptor.IsCode && descriptor.Conforming) || (descriptor.Dpl >= _cpl && descriptor.Dpl >= (selector & 3));
        if (!validType || !visible)
        {
            Set(Flag.Zero, false);
            return;
        }

        uint value = limit ? descriptor.Limit : descriptor.High & (size == 4 ? 0x00FF_FF00u : 0xFF00u);
        SetReg(_reg, size, value);
        Set(Flag.Zero, true);
    }

    /// <summary>0F 20 to 0F 26: MOV to and from the control, debug and test registers, always between registers.</summary>
    private void MoveSpecialRegister(byte opcode)
    {
        DecodeModRm();
        RequirePrivilege();
        int number = _reg;
        switch (opcode)
        {
            case 0x20:
                SetReg(_rm, 4, number switch
                {
                    0 => Cr0,
                    2 => Cr2,
                    3 => Cr3,
                    _ => throw InvalidOpcode(),
                });
                return;

            case 0x22:
                switch (number)
                {
                    case 0: WriteCr0(GetReg(_rm, 4)); return;
                    case 2: Cr2 = GetReg(_rm, 4); return;
                    case 3: Cr3 = GetReg(_rm, 4); return;
                    default: throw InvalidOpcode();
                }

            // DR4 and DR5 are DR6 and DR7 under other names
            case 0x21: SetReg(_rm, 4, DebugRegister(number)); return;
            case 0x23: SetDebugRegister(number, GetReg(_rm, 4)); return;
            case 0x24: SetReg(_rm, 4, _test[number]); return;
            default: _test[number] = GetReg(_rm, 4); return;
        }
    }

    private uint DebugRegister(int number) => number switch
    {
        4 or 6 => Dr6,
        5 or 7 => Dr7,
        _ => _debug[number],
    };

    private void SetDebugRegister(int number, uint value)
    {
        switch (number)
        {
            case 4 or 6: Dr6 = value; break;
            case 5 or 7: Dr7 = value; break;
            default: _debug[number] = value; break;
        }
    }

    /// <summary>ARPL: raises a selector's RPL to another's, setting zero if it did; an invalid opcode in real mode.</summary>
    private void AdjustRpl()
    {
        if (!ProtectedMode)
        {
            throw InvalidOpcode();
        }

        DecodeModRm();
        uint destination = ReadRm(2);
        uint source = GetReg(_reg, 2);
        if ((destination & 3) < (source & 3))
        {
            WriteRm(2, (destination & ~3u) | (source & 3));
            Set(Flag.Zero, true);
        }
        else
        {
            Set(Flag.Zero, false);
        }
    }

    // ---- for the DPMI host -------------------------------------------------------------------

    /// <summary>
    /// The processor's state as a program sees it: the registers, the flags, the mode, the
    /// privilege level, every segment register's descriptor cache, and the interrupt table, which
    /// differs between the modes. The machine's DPMI host
    /// switches between real and protected mode by saving one and restoring another, so the
    /// switch runs no code of its own.
    /// </summary>
    internal sealed class Context
    {
        internal readonly uint[] Registers = new uint[8];
        internal readonly SegmentCache[] Segments = new SegmentCache[6];
        internal uint Eip;
        internal uint Flags;
        internal uint Cr0;
        internal int Cpl;
        internal uint IdtBase;
        internal uint IdtLimit;

        public bool ProtectedMode => (Cr0 & 1) != 0;

        public ushort Ss => Segments[SS].Selector;

        public uint Esp => Registers[ESP];
    }

    /// <summary>The processor's state, to go back to later with <see cref="RestoreContext"/>.</summary>
    internal Context SaveContext()
    {
        var context = new Context { Eip = _eip, Flags = _flags, Cr0 = Cr0, Cpl = _cpl, IdtBase = _idtBase, IdtLimit = _idtLimit };
        _regs.CopyTo(context.Registers, 0);
        for (int segment = 0; segment < 6; segment++)
        {
            context.Segments[segment] = SaveCache(segment);
        }

        return context;
    }

    /// <summary>Puts back a state <see cref="SaveContext"/> took, mode and all.</summary>
    internal void RestoreContext(Context context)
    {
        context.Registers.CopyTo(_regs, 0);
        _eip = context.Eip;
        _flags = context.Flags;
        Cr0 = context.Cr0;
        _cpl = context.Cpl;
        (_idtBase, _idtLimit) = (context.IdtBase, context.IdtLimit);
        for (int segment = 0; segment < 6; segment++)
        {
            RestoreCache(segment, context.Segments[segment]);
        }

        Halted = false;
    }

    /// <summary>
    /// Switches to real mode with every segment register's cache as after reset: base 0, a
    /// 64 KB limit, 16-bit. The caller loads the segments and the registers.
    /// </summary>
    internal void EnterRealMode()
    {
        Cr0 &= ~1u;
        _cpl = 0;
        for (int segment = 0; segment < 6; segment++)
        {
            RestoreCache(segment, new SegmentCache(0, 0, 0xFFFF, segment == CS ? (byte)0x9B : (byte)0x93, false, SegmentUse.ReadWrite));
        }

        Halted = false;
    }

    /// <summary>
    /// Switches to protected mode at privilege level 0, leaving the segment caches as they are.
    /// The caller loads the segments with <see cref="TryLoadSelector"/>, CS first: its RPL
    /// becomes the privilege level, which the stack's must match.
    /// </summary>
    internal void EnterProtectedMode()
    {
        Cr0 |= 1;
        _cpl = 0;
        Halted = false;
    }

    /// <summary>
    /// Loads a segment register in protected mode with the processor's checks (CS without them,
    /// taking its RPL as the new privilege level); false, and nothing loaded, if they fail.
    /// </summary>
    internal bool TryLoadSelector(int segment, ushort selector)
    {
        try
        {
            LoadProtectedSegment(segment, selector);
            return true;
        }
        catch (CpuFault)
        {
            return false;
        }
    }

    /// <summary>Loads the local descriptor table and the task register, as LLDT and LTR would at level 0.</summary>
    internal void LoadSystemTables(ushort ldt, ushort task)
    {
        try
        {
            LoadLdt(ldt);
            LoadTaskRegister(task);
        }
        catch (CpuFault fault)
        {
            throw new InvalidOperationException($"The system tables wouldn't load: fault {fault.Vector}, error {fault.ErrorCode:X4}h.");
        }
    }

    /// <summary>A segment register's base, from its descriptor cache.</summary>
    internal uint SegmentBase(int segment) => _bases[segment];

    /// <summary>Whether a segment register holds a 32-bit segment: code defaulting to 32 bits, or a stack addressed by ESP.</summary>
    internal bool SegmentBig(int segment) => _big[segment];

    /// <summary>
    /// Takes an interrupt into a handler as if through an interrupt gate, without one in the
    /// table: how the DPMI host calls a program's handler for a hardware interrupt, whose vector
    /// in the table belongs to an exception. A fault on the way is raised as usual.
    /// </summary>
    internal void InterruptThrough(ushort selector, uint offset, bool gate32)
    {
        Halted = false;
        _start = _eip;
        _startEsp = _regs[ESP];
        uint type = gate32 ? (uint)SystemType.InterruptGate32 : (uint)SystemType.InterruptGate16;
        var gate = new Descriptor(((uint)selector << 16) | (offset & 0xFFFF), (offset & 0xFFFF_0000) | 0x8000 | (3u << 13) | (type << 8));
        try
        {
            EnterGate(gate, _eip, -1, 1);
        }
        catch (CpuFault fault)
        {
            _regs[ESP] = _startEsp;
            LastFault = fault.Vector;
            RaiseFault(fault);
        }
    }
}
