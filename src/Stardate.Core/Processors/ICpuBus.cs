namespace Stardate.Core.Processors;

/// <summary>
/// What a processor sees of the machine around it: memory, by physical address, and the separate
/// I/O port space. The machine decodes both to RAM, ROM and devices.
/// </summary>
/// <remarks>
/// Every access is one byte. A word or doubleword is made of consecutive byte accesses, which is
/// how the 8-bit devices on the PC's ISA bus (the timer, the interrupt controller, the Sound
/// Blaster, the VGA's index and data registers) see them anyway.
/// </remarks>
public interface ICpuBus
{
    /// <summary>The byte at the physical address, with whatever side effects reading it has.</summary>
    byte Read(uint address);

    /// <summary>Writes the byte to the physical address.</summary>
    void Write(uint address, byte value);

    /// <summary>The byte an <c>IN</c> reads from the port.</summary>
    byte In(ushort port);

    /// <summary>The byte an <c>OUT</c> writes to the port.</summary>
    void Out(ushort port, byte value);
}
