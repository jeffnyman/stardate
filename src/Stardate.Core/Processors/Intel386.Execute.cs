namespace Stardate.Core.Processors;

// Prefixes and the one-byte opcodes.
public sealed partial class Intel386
{
    private bool _lock;

    private void Execute()
    {
        _lock = false;
        while (true)
        {
            byte opcode = FetchByte();
            switch (opcode)
            {
                case 0x26: _segment = ES; continue;
                case 0x2E: _segment = CS; continue;
                case 0x36: _segment = SS; continue;
                case 0x3E: _segment = DS; continue;
                case 0x64: _segment = FS; continue;
                case 0x65: _segment = GS; continue;

                // these prefixes switch one instruction to the other operand or address size: to
                // 32 bits in real mode and 16-bit code, to 16 in 32-bit code
                case 0x66: _operand32 = !_big[CS]; continue;
                case 0x67: _address32 = !_big[CS]; continue;

                case 0xF0: _lock = true; continue;
                case 0xF2: _repeat = Repeat.WhileNotEqual; continue;
                case 0xF3: _repeat = Repeat.WhileEqual; continue;
            }

            if (_lock && !Lockable(opcode))
            {
                throw InvalidOpcode();
            }

            Run(opcode);
            return;
        }
    }

    /// <summary>
    /// Whether LOCK may prefix the instruction: only the read-modify-write instructions, and only
    /// when their destination is in memory. Any other use is an invalid opcode.
    /// </summary>
    private bool Lockable(byte opcode)
    {
        int Peek(int ahead) => _bus.Read(_bases[CS] + _eip + (uint)ahead);
        bool Memory(int modrm) => (modrm >> 6) != 3;

        if (opcode < 0x40 && opcode != 0x0F)
        {
            // ADD, OR, ADC, SBB, AND, SUB and XOR into memory; not CMP
            return (opcode & 7) <= 1 && (opcode >> 3) != 7 && Memory(Peek(0));
        }

        switch (opcode)
        {
            case 0x80: case 0x81: case 0x82: case 0x83:
                return ((Peek(0) >> 3) & 7) != 7 && Memory(Peek(0));
            case 0x86: case 0x87:
                return Memory(Peek(0));
            case 0xF6: case 0xF7:
                return ((Peek(0) >> 3) & 7) is 2 or 3 && Memory(Peek(0));
            case 0xFE: case 0xFF:
                return ((Peek(0) >> 3) & 7) <= 1 && Memory(Peek(0));
            case 0x0F:
                int second = Peek(0), modrm = Peek(1);
                return second switch
                {
                    0xAB or 0xB3 or 0xBB => Memory(modrm),
                    0xBA => ((modrm >> 3) & 7) >= 5 && Memory(modrm),
                    _ => false,
                };
            default:
                return false;
        }
    }

    private void Run(byte opcode)
    {
        int size = OperandSize;

        // 00h to 3Fh: eight arithmetic and logic operations, six forms of each
        if (opcode < 0x40 && (opcode & 7) < 6)
        {
            int operation = opcode >> 3;
            int width = (opcode & 1) == 0 ? 1 : size;
            switch (opcode & 7)
            {
                case 0:
                case 1:
                {
                    DecodeModRm();
                    uint result = Alu(operation, ReadRm(width), GetReg(_reg, width), width);
                    if (operation != 7)
                    {
                        WriteRm(width, result);
                    }

                    return;
                }

                case 2:
                case 3:
                {
                    DecodeModRm();
                    uint result = Alu(operation, GetReg(_reg, width), ReadRm(width), width);
                    if (operation != 7)
                    {
                        SetReg(_reg, width, result);
                    }

                    return;
                }

                default:
                {
                    uint result = Alu(operation, GetReg(EAX, width), FetchImmediate(width), width);
                    if (operation != 7)
                    {
                        SetReg(EAX, width, result);
                    }

                    return;
                }
            }
        }

        switch (opcode)
        {
            case 0x06: Push(_selectors[ES], size); return;
            case 0x07: PopSegment(ES); return;
            case 0x0E: Push(_selectors[CS], size); return;
            case 0x0F: RunExtended(FetchByte()); return;
            case 0x16: Push(_selectors[SS], size); return;
            case 0x17: PopSegment(SS); return;
            case 0x1E: Push(_selectors[DS], size); return;
            case 0x1F: PopSegment(DS); return;
            case 0x27: DecimalAdjustAdd(); return;
            case 0x2F: DecimalAdjustSubtract(); return;
            case 0x37: AsciiAdjust(subtract: false); return;
            case 0x3F: AsciiAdjust(subtract: true); return;

            case >= 0x40 and <= 0x47: SetReg(opcode & 7, size, Increment(GetReg(opcode & 7, size), size)); return;
            case >= 0x48 and <= 0x4F: SetReg(opcode & 7, size, Decrement(GetReg(opcode & 7, size), size)); return;

            // PUSH SP pushes SP as it was before the push, as on every x86 since the 286
            case >= 0x50 and <= 0x57: Push(GetReg(opcode & 7, size), size); return;
            case >= 0x58 and <= 0x5F: SetReg(opcode & 7, size, Pop(size)); return;

            case 0x60: PushAll(size); return;
            case 0x61: PopAll(size); return;
            case 0x62: Bound(size); return;
            case 0x63: AdjustRpl(); return;

            case 0x68: Push(FetchImmediate(size), size); return;
            case 0x69:
                DecodeModRm();
                SetReg(_reg, size, SignedMultiplyTruncated(ReadRm(size), FetchImmediate(size), size));
                return;
            case 0x6A: Push(FetchSignedByte(size), size); return;
            case 0x6B:
                DecodeModRm();
                SetReg(_reg, size, SignedMultiplyTruncated(ReadRm(size), FetchSignedByte(size), size));
                return;
            case >= 0x6C and <= 0x6F: StringInstruction(opcode); return;

            case >= 0x70 and <= 0x7F:
            {
                int displacement = (sbyte)FetchByte();
                if (Condition(opcode & 0x0F))
                {
                    JumpRelative(displacement);
                }

                return;
            }

            case 0x80:
            case 0x82:
                Group1(1, immediateSize: 1);
                return;
            case 0x81: Group1(size, immediateSize: size); return;
            case 0x83: Group1(size, immediateSize: 0); return;

            case 0x84:
            case 0x85:
            {
                int width = opcode == 0x84 ? 1 : size;
                DecodeModRm();
                Logic(ReadRm(width) & GetReg(_reg, width), width);
                return;
            }

            case 0x86:
            case 0x87:
            {
                int width = opcode == 0x86 ? 1 : size;
                DecodeModRm();
                uint value = ReadRm(width);
                WriteRm(width, GetReg(_reg, width));
                SetReg(_reg, width, value);
                return;
            }

            case 0x88: DecodeModRm(); WriteRm(1, GetReg(_reg, 1)); return;
            case 0x89: DecodeModRm(); WriteRm(size, GetReg(_reg, size)); return;
            case 0x8A: DecodeModRm(); SetReg(_reg, 1, ReadRm(1)); return;
            case 0x8B: DecodeModRm(); SetReg(_reg, size, ReadRm(size)); return;

            case 0x8C:
                DecodeModRm();
                if (_reg > GS)
                {
                    throw InvalidOpcode();
                }

                // a register destination takes the operand size; memory always gets a word
                WriteRm(_mod == 3 ? size : 2, _selectors[_reg]);
                return;

            case 0x8D:
                DecodeModRm();
                if (_mod == 3)
                {
                    throw InvalidOpcode();
                }

                SetReg(_reg, size, _eaOffset);
                return;

            case 0x8E:
                DecodeModRm();
                if (_reg == CS || _reg > GS)
                {
                    throw InvalidOpcode();
                }

                LoadSegment(_reg, (ushort)ReadRm(2));
                return;

            case 0x8F:
            {
                DecodeModRm();
                if (_reg != 0)
                {
                    throw InvalidOpcode();
                }

                uint value = Pop(size);
                if (_mod != 3 && _eaFromEsp)
                {
                    // an operand addressed through ESP is addressed after the pop
                    _eaOffset += (uint)size;
                }

                WriteRm(size, value);
                return;
            }

            case 0x90: return;
            case >= 0x91 and <= 0x97:
            {
                uint value = GetReg(opcode & 7, size);
                SetReg(opcode & 7, size, GetReg(EAX, size));
                SetReg(EAX, size, value);
                return;
            }

            case 0x98:
                // CBW, CWDE: sign-extend AL into AX, or AX into EAX
                SetReg(EAX, size, (uint)SignExtend(GetReg(EAX, size / 2), size / 2) & Mask(size));
                return;
            case 0x99:
                // CWD, CDQ: sign-extend AX into DX, or EAX into EDX
                SetReg(EDX, size, (GetReg(EAX, size) & SignBit(size)) != 0 ? Mask(size) : 0);
                return;

            case 0x9A:
            {
                uint offset = FetchImmediate(size);
                ushort selector = FetchWord();
                Push(_selectors[CS], size);
                Push(_eip, size);
                JumpFar(selector, offset);
                return;
            }

            // WAIT: there's no coprocessor to wait for
            case 0x9B: return;

            // PUSHFD pushes the virtual-8086 and resume flags as clear, and the 386 has no flags above them
            case 0x9C: Push(_flags & 0xFFFF, size); return;
            case 0x9D: SetFlags(Pop(size), size); return;
            case 0x9E:
                // SAHF: AH into sign, zero, adjust, parity and carry
                _flags = (_flags & ~0xD5u) | ((GetReg(EAX, 2) >> 8) & 0xD5);
                return;
            case 0x9F: SetReg(EAX, 2, (GetReg(EAX, 2) & 0xFF) | ((_flags & 0xFF) << 8)); return;

            case >= 0xA0 and <= 0xA3:
            {
                int width = (opcode & 1) == 0 ? 1 : size;
                uint offset = _address32 ? FetchDword() : FetchWord();
                int segment = _segment >= 0 ? _segment : DS;
                if (opcode < 0xA2)
                {
                    SetReg(EAX, width, ReadMemory(segment, offset, width));
                }
                else
                {
                    WriteMemory(segment, offset, width, GetReg(EAX, width));
                }

                return;
            }

            case >= 0xA4 and <= 0xA7: StringInstruction(opcode); return;
            case 0xA8: Logic(GetReg(EAX, 1) & FetchByte(), 1); return;
            case 0xA9: Logic(GetReg(EAX, size) & FetchImmediate(size), size); return;
            case >= 0xAA and <= 0xAF: StringInstruction(opcode); return;

            case >= 0xB0 and <= 0xB7: SetReg(opcode & 7, 1, FetchByte()); return;
            case >= 0xB8 and <= 0xBF: SetReg(opcode & 7, size, FetchImmediate(size)); return;

            case 0xC0: DecodeModRm(); ShiftRm(1, FetchByte()); return;
            case 0xC1: DecodeModRm(); ShiftRm(size, FetchByte()); return;

            case 0xC2:
            {
                ushort release = FetchWord();
                uint target = Pop(size);
                SetEip(target);
                SetSp(_regs[ESP] + release);
                return;
            }

            case 0xC3: SetEip(Pop(size)); return;

            case 0xC4: LoadFarPointer(ES, size); return;
            case 0xC5: LoadFarPointer(DS, size); return;

            case 0xC6:
            case 0xC7:
            {
                int width = opcode == 0xC6 ? 1 : size;
                DecodeModRm();
                if (_reg != 0)
                {
                    throw InvalidOpcode();
                }

                WriteRm(width, FetchImmediate(width));
                return;
            }

            case 0xC8: Enter(size); return;
            case 0xC9:
                SetSp(_regs[EBP]);
                SetReg(EBP, size, Pop(size));
                return;

            case 0xCA:
            case 0xCB:
            {
                ushort release = opcode == 0xCA ? FetchWord() : (ushort)0;
                if (ProtectedMode)
                {
                    ProtectedReturn(size, release);
                    return;
                }

                uint offset = Pop(size);
                ushort selector = (ushort)Pop(size);
                JumpFar(selector, offset);
                SetSp(_regs[ESP] + release);
                return;
            }

            case 0xCC: EnterInterrupt(Vector.Breakpoint, _eip); return;
            case 0xCD:
            {
                byte vector = FetchByte();
                EnterInterrupt(vector, _eip);
                return;
            }

            case 0xCE:
                if (Is(Flag.Overflow))
                {
                    EnterInterrupt(Vector.Overflow, _eip);
                }

                return;

            case 0xCF:
            {
                if (ProtectedMode)
                {
                    ProtectedInterruptReturn(size);
                    return;
                }

                uint offset = Pop(size);
                ushort selector = (ushort)Pop(size);
                uint flags = Pop(size);
                JumpFar(selector, offset);
                SetFlags(flags, size);
                return;
            }

            case 0xD0: DecodeModRm(); ShiftRm(1, 1); return;
            case 0xD1: DecodeModRm(); ShiftRm(size, 1); return;
            case 0xD2: DecodeModRm(); ShiftRm(1, (int)GetReg(ECX, 1)); return;
            case 0xD3: DecodeModRm(); ShiftRm(size, (int)GetReg(ECX, 1)); return;
            case 0xD4: AsciiAdjustMultiply(FetchByte()); return;
            case 0xD5: AsciiAdjustDivide(FetchByte()); return;

            // SALC, undocumented: AL to FFh when carry is set, 00h when it's clear
            case 0xD6: SetReg(EAX, 1, Is(Flag.Carry) ? 0xFFu : 0); return;

            case 0xD7:
            {
                // XLAT: AL becomes the byte at BX + AL
                uint mask = _address32 ? 0xFFFF_FFFF : 0xFFFF;
                uint offset = (_regs[EBX] + GetReg(EAX, 1)) & mask;
                SetReg(EAX, 1, ReadMemory(_segment >= 0 ? _segment : DS, offset, 1));
                return;
            }

            // the coprocessor's instructions: with no coprocessor fitted they take their operand
            // and do nothing, which is how a program finds there's none
            case >= 0xD8 and <= 0xDF: DecodeModRm(); return;

            case >= 0xE0 and <= 0xE3: Loop(opcode); return;

            case 0xE4: SetReg(EAX, 1, PortIn(FetchByte(), 1)); return;
            case 0xE5: SetReg(EAX, size, PortIn(FetchByte(), size)); return;
            case 0xE6: PortOut(FetchByte(), 1, GetReg(EAX, 1)); return;
            case 0xE7: PortOut(FetchByte(), size, GetReg(EAX, size)); return;

            case 0xE8:
            {
                uint displacement = FetchImmediate(size);
                Push(_eip, size);
                JumpRelative((int)displacement);
                return;
            }

            case 0xE9: JumpRelative((int)FetchImmediate(size)); return;
            case 0xEA:
            {
                uint offset = FetchImmediate(size);
                JumpFar(FetchWord(), offset);
                return;
            }

            case 0xEB: JumpRelative((sbyte)FetchByte()); return;

            case 0xEC: SetReg(EAX, 1, PortIn((ushort)_regs[EDX], 1)); return;
            case 0xED: SetReg(EAX, size, PortIn((ushort)_regs[EDX], size)); return;
            case 0xEE: PortOut((ushort)_regs[EDX], 1, GetReg(EAX, 1)); return;
            case 0xEF: PortOut((ushort)_regs[EDX], size, GetReg(EAX, size)); return;

            // ICEBP, undocumented: the debug exception, as a trap, through its gate whatever the gate's privilege
            case 0xF1: EnterInterrupt(Vector.Debug, _eip, -1, External.No); return;

            case 0xF4:
                RequirePrivilege();
                Halted = true;
                return;
            case 0xF5: _flags ^= Flag.Carry; return;
            case 0xF6: Group3(1); return;
            case 0xF7: Group3(size); return;
            case 0xF8: Set(Flag.Carry, false); return;
            case 0xF9: Set(Flag.Carry, true); return;
            case 0xFA: RequireIoPrivilege(); Set(Flag.Interrupt, false); return;
            case 0xFB: RequireIoPrivilege(); Set(Flag.Interrupt, true); return;
            case 0xFC: Set(Flag.Direction, false); return;
            case 0xFD: Set(Flag.Direction, true); return;
            case 0xFE: Group4(); return;
            case 0xFF: Group5(size); return;

            // ARPL, and everything else, isn't an instruction in real mode
            default: throw InvalidOpcode();
        }
    }

    // ---- control transfer --------------------------------------------------------------------

    /// <summary>Sets the instruction pointer, checking it against the code segment's limit.</summary>
    private void SetEip(uint target)
    {
        if (target > _limits[CS])
        {
            throw Fault(Vector.GeneralProtection);
        }

        _eip = target;
    }

    /// <summary>A relative jump: with 16-bit operands the target wraps within the segment.</summary>
    private void JumpRelative(int displacement)
    {
        uint target = _eip + (uint)displacement;
        SetEip(_operand32 ? target : target & 0xFFFF);
    }

    private void JumpFar(ushort selector, uint offset)
    {
        if (!_operand32)
        {
            offset &= 0xFFFF;
        }

        if (ProtectedMode)
        {
            ProtectedJump(selector, offset);
            return;
        }

        // in real mode loading CS keeps the limit, so the offset can be checked before the load
        SetEip(offset);
        LoadSegment(CS, selector);
    }

    /// <summary>LOOP, LOOPE, LOOPNE and JCXZ, counting with CX or, under 32-bit addressing, ECX.</summary>
    private void Loop(byte opcode)
    {
        int displacement = (sbyte)FetchByte();
        uint mask = _address32 ? 0xFFFF_FFFF : 0xFFFF;
        uint count = _regs[ECX] & mask;
        bool jump;

        if (opcode == 0xE3)
        {
            jump = count == 0;
        }
        else
        {
            count = (count - 1) & mask;
            _regs[ECX] = (_regs[ECX] & ~mask) | count;
            jump = count != 0 && opcode switch
            {
                0xE0 => !Is(Flag.Zero),
                0xE1 => Is(Flag.Zero),
                _ => true,
            };
        }

        if (jump)
        {
            JumpRelative(displacement);
        }
    }

    /// <summary>
    /// POPF and IRET's flags. In real mode every flag can be written, the I/O privilege level and
    /// nested-task bits included; the virtual-8086 bit can't be set this way. In protected mode
    /// the I/O privilege level changes only at privilege 0, and the interrupt flag only at a level
    /// the I/O privilege level allows; otherwise they're left as they were, without a fault.
    /// </summary>
    private void SetFlags(uint value, int size)
    {
        uint changeable = size == 2 ? 0x7FD5u : 0x0001_7FD5u;
        if (ProtectedMode)
        {
            if (_cpl > 0)
            {
                changeable &= ~IoplMask;
            }

            if (_cpl > Iopl)
            {
                changeable &= ~Flag.Interrupt;
            }
        }

        _flags = (_flags & ~changeable) | (value & changeable) | 0x0002;
    }

    // ---- groups ------------------------------------------------------------------------------

    /// <summary>
    /// Group 1 (80h to 83h): the arithmetic operations with an immediate. An immediate size of 0
    /// is a byte sign-extended to the operand.
    /// </summary>
    private void Group1(int size, int immediateSize)
    {
        DecodeModRm();
        uint value = ReadRm(size);
        uint immediate = immediateSize == 0 ? FetchSignedByte(size) : FetchImmediate(immediateSize);
        uint result = Alu(_reg, value, immediate, size);
        if (_reg != 7)
        {
            WriteRm(size, result);
        }
    }

    private void ShiftRm(int size, int count)
    {
        uint value = ReadRm(size);
        if ((count & 0x1F) != 0)
        {
            WriteRm(size, Shift(_reg, value, count, size));
        }
    }

    /// <summary>Group 3 (F6h, F7h): TEST, TEST again (undocumented), NOT, NEG, MUL, IMUL, DIV, IDIV.</summary>
    private void Group3(int size)
    {
        DecodeModRm();
        uint value = ReadRm(size);
        switch (_reg)
        {
            case 0:
            case 1:
                Logic(value & FetchImmediate(size), size);
                break;
            case 2: WriteRm(size, ~value & Mask(size)); break;
            case 3:
                WriteRm(size, Subtract(0, value, 0, size));
                break;
            case 4: Multiply(value, size); break;
            case 5: SignedMultiply(value, size); break;
            case 6: Divide(value, size); break;
            default: SignedDivide(value, size); break;
        }
    }

    /// <summary>Group 4 (FEh): INC and DEC of a byte.</summary>
    private void Group4()
    {
        DecodeModRm();
        switch (_reg)
        {
            case 0: WriteRm(1, Increment(ReadRm(1), 1)); break;
            case 1: WriteRm(1, Decrement(ReadRm(1), 1)); break;
            default: throw InvalidOpcode();
        }
    }

    /// <summary>Group 5 (FFh): INC, DEC, CALL, CALL far, JMP, JMP far, PUSH.</summary>
    private void Group5(int size)
    {
        DecodeModRm();
        switch (_reg)
        {
            case 0: WriteRm(size, Increment(ReadRm(size), size)); break;
            case 1: WriteRm(size, Decrement(ReadRm(size), size)); break;
            case 2:
            {
                uint target = ReadRm(size);
                Push(_eip, size);
                SetEip(target);
                break;
            }

            case 3:
            case 5:
            {
                if (_mod == 3)
                {
                    throw InvalidOpcode();
                }

                uint offset = ReadMemory(_eaSegment, _eaOffset, size);
                ushort selector = (ushort)ReadMemory(_eaSegment, NextOffset(_eaOffset, size), 2);
                if (_reg == 3)
                {
                    Push(_selectors[CS], size);
                    Push(_eip, size);
                }

                JumpFar(selector, offset);
                break;
            }

            case 4: SetEip(ReadRm(size)); break;
            case 6: Push(ReadRm(size), size); break;
            default: throw InvalidOpcode();
        }
    }

    /// <summary>The offset a number of bytes on from another, wrapped under 16-bit addressing.</summary>
    private uint NextOffset(uint offset, int bytes)
    {
        uint next = offset + (uint)bytes;
        return _address32 ? next : next & 0xFFFF;
    }

    // ---- other instructions with more to them ------------------------------------------------

    /// <summary>
    /// POP into a segment register. With a 32-bit operand SP still moves by four, but only the
    /// word holding the selector is read, so a pop at the very top of the segment doesn't fault.
    /// </summary>
    private void PopSegment(int segment)
    {
        // ESP moves by the stack's width before the load, which for POP SS may change it; if the
        // load faults, the instruction is undone and ESP with it
        uint sp = _regs[ESP] & StackMask;
        ushort selector = (ushort)ReadMemory(SS, sp, 2);
        SetSp(sp + (uint)OperandSize);
        LoadSegment(segment, selector);
    }

    /// <summary>LES, LDS, LSS, LFS and LGS: an offset and a segment from a far pointer in memory.</summary>
    private void LoadFarPointer(int segment, int size)
    {
        DecodeModRm();
        if (_mod == 3)
        {
            throw InvalidOpcode();
        }

        uint offset = ReadMemory(_eaSegment, _eaOffset, size);
        ushort selector = (ushort)ReadMemory(_eaSegment, NextOffset(_eaOffset, size), 2);
        LoadSegment(segment, selector);
        SetReg(_reg, size, offset);
    }

    /// <summary>
    /// PUSHA: AX, CX, DX, BX, the SP from before, BP, SI and DI, as eight pushes would leave them.
    /// The 386 writes them from the bottom up, DI first, so when the block runs off the end of the
    /// stack segment the registers below the fault are already written.
    /// </summary>
    private void PushAll(int size)
    {
        uint mask = StackMask;
        uint sp = _regs[ESP] & mask;
        uint top = (sp - (uint)(8 * size)) & mask;
        for (int reg = EDI; reg >= EAX; reg--)
        {
            uint value = reg == ESP ? _regs[ESP] & Mask(size) : GetReg(reg, size);
            WriteMemory(SS, (top + (uint)((EDI - reg) * size)) & mask, size, value);
        }

        SetSp(top);
    }

    /// <summary>
    /// POPA: the same in reverse, the saved SP skipped. Skipped, that is, but for one thing:
    /// POPAD on the 386 still loads the top half of ESP from it.
    /// </summary>
    private void PopAll(int size)
    {
        for (int reg = EDI; reg >= EAX; reg--)
        {
            uint value = Pop(size);
            if (reg != ESP)
            {
                SetReg(reg, size, value);
            }
            else if (size == 4)
            {
                _regs[ESP] = (value & 0xFFFF_0000) | (_regs[ESP] & 0xFFFF);
            }
        }
    }

    /// <summary>BOUND: a bound-range fault unless the signed index lies within a pair of bounds in memory.</summary>
    private void Bound(int size)
    {
        DecodeModRm();
        if (_mod == 3)
        {
            throw InvalidOpcode();
        }

        int index = SignExtend(GetReg(_reg, size), size);
        int lower = SignExtend(ReadMemory(_eaSegment, _eaOffset, size), size);
        int upper = SignExtend(ReadMemory(_eaSegment, NextOffset(_eaOffset, size), size), size);
        if (index < lower || index > upper)
        {
            throw Fault(Vector.Bound);
        }
    }

    /// <summary>
    /// ENTER: makes a stack frame for a procedure, copying the frame pointers of up to 31
    /// enclosing procedures for languages with nested procedures.
    /// </summary>
    private void Enter(int size)
    {
        ushort allocate = FetchWord();
        int level = FetchByte() & 0x1F;

        Push(GetReg(EBP, size), size);
        uint mask = StackMask;
        uint frame = _regs[ESP] & mask;

        if (level > 0)
        {
            uint bp = _regs[EBP];
            for (int i = 1; i < level; i++)
            {
                bp = (bp - (uint)size) & mask;
                Push(ReadMemory(SS, bp, size), size);
            }

            Push(frame, size);
        }

        SetReg(EBP, size, frame);
        SetSp(_regs[ESP] - allocate);
    }

    private uint PortIn(ushort port, int size)
    {
        CheckPort(port, size);
        uint value = 0;
        for (int i = 0; i < size; i++)
        {
            value |= (uint)_bus.In((ushort)(port + i)) << (8 * i);
        }

        return value;
    }

    private void PortOut(ushort port, int size, uint value)
    {
        CheckPort(port, size);
        for (int i = 0; i < size; i++)
        {
            _bus.Out((ushort)(port + i), (byte)(value >> (8 * i)));
        }
    }
}
