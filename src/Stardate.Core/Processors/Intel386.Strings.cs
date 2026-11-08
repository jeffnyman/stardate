namespace Stardate.Core.Processors;

// The string instructions: MOVS, CMPS, STOS, LODS, SCAS, INS and OUTS, with their repeat prefixes.
public sealed partial class Intel386
{
    /// <summary>
    /// Runs a string instruction, every repetition of it under a REP prefix. The source is DS:SI
    /// (or another segment, by prefix) and the destination ES:DI, always ES. SI, DI and CX are the
    /// 16-bit registers, or ESI, EDI and ECX under 32-bit addressing. The direction flag chooses
    /// whether they count up or down.
    /// </summary>
    private void StringInstruction(byte opcode)
    {
        int size = (opcode & 1) == 0 ? 1 : OperandSize;
        uint mask = _address32 ? 0xFFFF_FFFF : 0xFFFF;
        int source = _segment >= 0 ? _segment : DS;
        uint step = Is(Flag.Direction) ? (uint)-size : (uint)size;

        // CMPS and SCAS stop early under REPE or REPNE; the others repeat CX times
        bool compares = opcode is 0xA6 or 0xA7 or 0xAE or 0xAF;

        if (_repeat == Repeat.None)
        {
            Once();
            return;
        }

        int left = RepeatLimit > 0 ? RepeatLimit : int.MaxValue;
        while ((_regs[ECX] & mask) != 0)
        {
            if (left-- == 0)
            {
                // to be carried on: the instruction, prefixes and all, runs again next step
                _eip = _start;
                return;
            }

            Once();
            Repetitions++;
            _regs[ECX] = (_regs[ECX] & ~mask) | ((_regs[ECX] - 1) & mask);

            if (compares && Is(Flag.Zero) != (_repeat == Repeat.WhileEqual))
            {
                break;
            }
        }

        void Once()
        {
            uint si = _regs[ESI] & mask, di = _regs[EDI] & mask;
            switch (opcode)
            {
                case 0x6C:
                case 0x6D:
                    WriteMemory(ES, di, size, PortIn((ushort)_regs[EDX], size));
                    Advance(EDI);
                    break;
                case 0x6E:
                case 0x6F:
                    PortOut((ushort)_regs[EDX], size, ReadMemory(source, si, size));
                    Advance(ESI);
                    break;
                case 0xA4:
                case 0xA5:
                    WriteMemory(ES, di, size, ReadMemory(source, si, size));
                    Advance(ESI);
                    Advance(EDI);
                    break;
                case 0xA6:
                case 0xA7:
                    Subtract(ReadMemory(source, si, size), ReadMemory(ES, di, size), 0, size);
                    Advance(ESI);
                    Advance(EDI);
                    break;
                case 0xAA:
                case 0xAB:
                    WriteMemory(ES, di, size, GetReg(EAX, size));
                    Advance(EDI);
                    break;
                case 0xAC:
                case 0xAD:
                    SetReg(EAX, size, ReadMemory(source, si, size));
                    Advance(ESI);
                    break;
                default:
                    Subtract(GetReg(EAX, size), ReadMemory(ES, di, size), 0, size);
                    Advance(EDI);
                    break;
            }
        }

        void Advance(int reg) => _regs[reg] = (_regs[reg] & ~mask) | ((_regs[reg] + step) & mask);
    }
}
