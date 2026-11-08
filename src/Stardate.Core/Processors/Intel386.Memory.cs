using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Stardate.Core.Processors;

// Fetching, operand decoding, and memory and stack access, all through segments.
public sealed partial class Intel386
{
    // the ModR/M byte of the instruction in progress, split into its fields, and the memory operand
    // it names when mod isn't 3: a segment and an offset
    private int _mod;
    private int _reg;
    private int _rm;
    private int _eaSegment;
    private uint _eaOffset;

    // whether that offset was based on ESP: POP into such an operand addresses it after the pop
    private bool _eaFromEsp;

    private int OperandSize => _operand32 ? 4 : 2;

    // ---- instruction fetch -------------------------------------------------------------------

    private byte FetchByte()
    {
        // an instruction can't be longer than 15 bytes, prefixes included
        if (++_length > 15)
        {
            throw Fault(Vector.GeneralProtection);
        }

        if (_eip > _limits[CS])
        {
            throw Fault(Vector.GeneralProtection);
        }

        uint address = _bases[CS] + _eip;
        _eip++;
        return IsPlain(_plainRead, address, 1) ? _ram![address] : _bus.Read(address);
    }

    /// <summary>Whether an access of the given size lies in one page of plain memory, to be made directly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPlain(bool[]? map, uint address, int size)
    {
        uint page = address >> 12;
        return map is not null && page < (uint)map.Length && (address & 0xFFF) <= 0x1000 - size && map[page];
    }

    private ushort FetchWord() => (ushort)(FetchByte() | (FetchByte() << 8));

    private uint FetchDword() => FetchWord() | ((uint)FetchWord() << 16);

    /// <summary>An immediate of the given size in bytes.</summary>
    private uint FetchImmediate(int size) => size switch
    {
        1 => FetchByte(),
        2 => FetchWord(),
        _ => FetchDword(),
    };

    /// <summary>A byte immediate, sign-extended to the given size.</summary>
    private uint FetchSignedByte(int size) => (uint)(sbyte)FetchByte() & Mask(size);

    // ---- ModR/M ------------------------------------------------------------------------------

    /// <summary>
    /// Reads the ModR/M byte, and the SIB byte and displacement that follow it, and works out the
    /// memory operand when there is one.
    /// </summary>
    private void DecodeModRm()
    {
        byte modrm = FetchByte();
        _mod = modrm >> 6;
        _reg = (modrm >> 3) & 7;
        _rm = modrm & 7;
        _eaFromEsp = false;

        if (_mod == 3)
        {
            return;
        }

        if (_address32)
        {
            Decode32();
        }
        else
        {
            Decode16();
        }

        if (_segment >= 0)
        {
            _eaSegment = _segment;
        }
    }

    /// <summary>
    /// The 8086's addressing: a base (BX or BP), an index (SI or DI), either, or both, plus a
    /// displacement, wrapped to 16 bits. Addresses based on BP are in the stack segment.
    /// </summary>
    private void Decode16()
    {
        uint offset;
        _eaSegment = DS;
        switch (_rm)
        {
            case 0: offset = _regs[EBX] + _regs[ESI]; break;
            case 1: offset = _regs[EBX] + _regs[EDI]; break;
            case 2: offset = _regs[EBP] + _regs[ESI]; _eaSegment = SS; break;
            case 3: offset = _regs[EBP] + _regs[EDI]; _eaSegment = SS; break;
            case 4: offset = _regs[ESI]; break;
            case 5: offset = _regs[EDI]; break;
            case 6:
                if (_mod == 0)
                {
                    // no base: the operand is a bare 16-bit address
                    offset = FetchWord();
                }
                else
                {
                    offset = _regs[EBP];
                    _eaSegment = SS;
                }

                break;
            default: offset = _regs[EBX]; break;
        }

        if (_mod == 1)
        {
            offset += (uint)(sbyte)FetchByte();
        }
        else if (_mod == 2)
        {
            offset += FetchWord();
        }

        _eaOffset = offset & 0xFFFF;
    }

    /// <summary>
    /// The 386's addressing: any general register as base, any but ESP as an index scaled by 1,
    /// 2, 4 or 8 (through the SIB byte), and an 8- or 32-bit displacement. Addresses based on EBP
    /// or ESP are in the stack segment.
    /// </summary>
    private void Decode32()
    {
        uint offset;
        _eaSegment = DS;

        if (_rm == 4)
        {
            byte sib = FetchByte();
            int scale = sib >> 6, index = (sib >> 3) & 7, baseReg = sib & 7;

            if (baseReg == 5 && _mod == 0)
            {
                // no base: a 32-bit displacement stands in for it
                offset = FetchDword();
            }
            else
            {
                offset = _regs[baseReg];
                if (baseReg == ESP || baseReg == EBP)
                {
                    _eaSegment = SS;
                }

                _eaFromEsp = baseReg == ESP;
            }

            // index 4 (ESP) means no index. Intel says the scale must then be 0; when it isn't,
            // the 386 applies it to the base instead
            if (index != 4)
            {
                offset += _regs[index] << scale;
            }
            else if (!(baseReg == 5 && _mod == 0))
            {
                offset <<= scale;
            }
        }
        else if (_rm == 5 && _mod == 0)
        {
            // no base and no index: the operand is a bare 32-bit address
            _eaOffset = FetchDword();
            return;
        }
        else
        {
            offset = _regs[_rm];
            if (_rm == EBP)
            {
                _eaSegment = SS;
            }
        }

        if (_mod == 1)
        {
            offset += (uint)(sbyte)FetchByte();
        }
        else if (_mod == 2)
        {
            offset += FetchDword();
        }

        _eaOffset = offset;
    }

    /// <summary>A register as instructions number them, of the given size in bytes.</summary>
    private uint GetReg(int reg, int size) => size switch
    {
        // the byte registers: AL, CL, DL, BL, then AH, CH, DH, BH
        1 => reg < 4 ? _regs[reg] & 0xFF : (_regs[reg - 4] >> 8) & 0xFF,
        2 => _regs[reg] & 0xFFFF,
        _ => _regs[reg],
    };

    private void SetReg(int reg, int size, uint value)
    {
        switch (size)
        {
            case 1:
                if (reg < 4)
                {
                    _regs[reg] = (_regs[reg] & 0xFFFF_FF00) | (value & 0xFF);
                }
                else
                {
                    _regs[reg - 4] = (_regs[reg - 4] & 0xFFFF_00FF) | ((value & 0xFF) << 8);
                }

                break;
            case 2:
                _regs[reg] = (_regs[reg] & 0xFFFF_0000) | (value & 0xFFFF);
                break;
            default:
                _regs[reg] = value;
                break;
        }
    }

    /// <summary>The ModR/M operand: a register when mod is 3, otherwise memory.</summary>
    private uint ReadRm(int size) => _mod == 3 ? GetReg(_rm, size) : ReadMemory(_eaSegment, _eaOffset, size);

    private void WriteRm(int size, uint value)
    {
        if (_mod == 3)
        {
            SetReg(_rm, size, value);
        }
        else
        {
            WriteMemory(_eaSegment, _eaOffset, size, value);
        }
    }

    // ---- memory through segments -------------------------------------------------------------

    /// <summary>
    /// The linear address of an access, after checking that all of it lies within the segment's
    /// limit and that the segment allows it. In real mode the limit is 64 KB, so a word at offset
    /// FFFFh faults rather than wrapping as it did on the 8086: a stack fault in the stack segment,
    /// a general protection fault in any other.
    /// </summary>
    /// <remarks>
    /// In protected mode a segment may be read-only, or code that can't be read, or unusable
    /// (a null selector); and an expand-down segment, for a stack that grows down, holds the
    /// offsets above its limit rather than those up to it.
    /// </remarks>
    private uint Linear(int segment, uint offset, int size, bool write = false)
    {
        var use = _use[segment];
        if ((use & (write ? SegmentUse.Write : SegmentUse.Read)) == 0)
        {
            throw Fault(segment == SS ? Vector.StackFault : Vector.GeneralProtection);
        }

        uint limit = _limits[segment];
        uint last = offset + (uint)(size - 1);
        bool outside = (use & SegmentUse.ExpandDown) == 0
            ? offset > limit || limit - offset < (uint)(size - 1)
            : offset <= limit || last > (_big[segment] ? 0xFFFF_FFFF : 0xFFFF) || last < offset;
        if (outside)
        {
            throw Fault(segment == SS ? Vector.StackFault : Vector.GeneralProtection);
        }

        return _bases[segment] + offset;
    }

    private uint ReadMemory(int segment, uint offset, int size)
    {
        uint address = Linear(segment, offset, size);
        if (IsPlain(_plainRead, address, size))
        {
            ref byte at = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ram!), (nint)address);
            return size switch
            {
                1 => at,
                2 => Unsafe.ReadUnaligned<ushort>(ref at),
                _ => Unsafe.ReadUnaligned<uint>(ref at),
            };
        }

        uint value = _bus.Read(address);
        for (int i = 1; i < size; i++)
        {
            value |= (uint)_bus.Read(address + (uint)i) << (8 * i);
        }

        return value;
    }

    private void WriteMemory(int segment, uint offset, int size, uint value)
    {
        uint address = Linear(segment, offset, size, write: true);
        if (IsPlain(_plainWrite, address, size))
        {
            ref byte at = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ram!), (nint)address);
            switch (size)
            {
                case 1: at = (byte)value; break;
                case 2: Unsafe.WriteUnaligned(ref at, (ushort)value); break;
                default: Unsafe.WriteUnaligned(ref at, value); break;
            }

            return;
        }

        for (int i = 0; i < size; i++)
        {
            _bus.Write(address + (uint)i, (byte)(value >> (8 * i)));
        }
    }

    // ---- the stack ---------------------------------------------------------------------------

    // a 16-bit stack, real mode's, is addressed by SP and leaves the top half of ESP as it is; a
    // 32-bit stack segment's is addressed by the whole of ESP

    /// <summary>The part of ESP that addresses the stack: SP, or all of ESP for a 32-bit stack segment.</summary>
    private uint StackMask => _big[SS] ? 0xFFFF_FFFF : 0xFFFF;

    private void Push(uint value, int size)
    {
        uint mask = StackMask;
        uint sp = (_regs[ESP] - (uint)size) & mask;
        WriteMemory(SS, sp, size, value);
        _regs[ESP] = (_regs[ESP] & ~mask) | sp;
    }

    private uint Pop(int size)
    {
        uint mask = StackMask;
        uint sp = _regs[ESP] & mask;
        uint value = ReadMemory(SS, sp, size);
        _regs[ESP] = (_regs[ESP] & ~mask) | ((sp + (uint)size) & mask);
        return value;
    }

    /// <summary>Reads from the stack at a distance above its top, without popping.</summary>
    private uint PeekStack(int above, int size) => ReadMemory(SS, (_regs[ESP] + (uint)above) & StackMask, size);

    private void SetSp(uint sp)
    {
        uint mask = StackMask;
        _regs[ESP] = (_regs[ESP] & ~mask) | (sp & mask);
    }
}
