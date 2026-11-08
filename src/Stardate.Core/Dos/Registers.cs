using Stardate.Core.Processors;

namespace Stardate.Core.Dos;

/// <summary>
/// The processor's registers by their 16- and 8-bit names, as DOS and BIOS calls use them. Writing
/// one leaves the rest of its 32-bit register as it was.
/// </summary>
internal readonly struct Registers(Machine machine)
{
    private Intel386 Cpu => machine.Cpu;

    public ushort Ax { get => (ushort)Cpu.Eax; set => Cpu.Eax = (Cpu.Eax & 0xFFFF_0000) | value; }

    public ushort Bx { get => (ushort)Cpu.Ebx; set => Cpu.Ebx = (Cpu.Ebx & 0xFFFF_0000) | value; }

    public ushort Cx { get => (ushort)Cpu.Ecx; set => Cpu.Ecx = (Cpu.Ecx & 0xFFFF_0000) | value; }

    public ushort Dx { get => (ushort)Cpu.Edx; set => Cpu.Edx = (Cpu.Edx & 0xFFFF_0000) | value; }

    public ushort Si { get => (ushort)Cpu.Esi; set => Cpu.Esi = (Cpu.Esi & 0xFFFF_0000) | value; }

    public ushort Di => (ushort)Cpu.Edi;

    public byte Al { get => (byte)Ax; set => Ax = (ushort)((Ax & 0xFF00) | value); }

    public byte Ah { get => (byte)(Ax >> 8); set => Ax = (ushort)((Ax & 0x00FF) | (value << 8)); }

    public byte Bl { get => (byte)Bx; set => Bx = (ushort)((Bx & 0xFF00) | value); }

    public byte Bh { get => (byte)(Bx >> 8); set => Bx = (ushort)((Bx & 0x00FF) | (value << 8)); }

    public byte Cl { get => (byte)Cx; set => Cx = (ushort)((Cx & 0xFF00) | value); }

    public byte Ch { get => (byte)(Cx >> 8); set => Cx = (ushort)((Cx & 0x00FF) | (value << 8)); }

    public byte Dl { get => (byte)Dx; set => Dx = (ushort)((Dx & 0xFF00) | value); }

    public byte Dh { get => (byte)(Dx >> 8); set => Dx = (ushort)((Dx & 0x00FF) | (value << 8)); }

    public ushort Ds { get => Cpu.Ds; set => Cpu.Ds = value; }

    public ushort Es { get => Cpu.Es; set => Cpu.Es = value; }
}
