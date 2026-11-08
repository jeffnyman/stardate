namespace Stardate.Core.Devices;

/// <summary>
/// The Intel 8259 programmable interrupt controller. The AT has two: the first takes the eight
/// hardware interrupt lines IRQ 0 (the timer) to IRQ 7, and passes them to the processor one at a
/// time, the lowest-numbered first, as interrupt vectors 8 to 15. The second takes IRQ 8 to 15,
/// as vectors 70h to 77h, and passes them through the first's IRQ 2, so they come between IRQ 1
/// and IRQ 3 in priority.
/// </summary>
/// <remarks>
/// A device raising its line sets that IRQ's bit in the request register. If its bit in the mask
/// register is clear and no IRQ of higher priority is being serviced, the controller asks the
/// processor for an interrupt, and when the processor takes it, the bit moves to the in-service
/// register. It stays there, holding off that IRQ and every lower one, until the handler sends
/// an end-of-interrupt command (20h to its first port). A handler that forgets gets no more
/// interrupts. An interrupt from the second controller is in service on both, so its handler
/// ends it on both: 20h to port A0h, then to port 20h.
/// </remarks>
internal sealed class InterruptController
{
    // the line the second controller is wired to on the first
    private const int Cascade = 2;

    private readonly InterruptController? _second;

    // the first controller, for the second: told when the second's line changes
    private InterruptController? _first;

    // the line it would give the processor now, or -1: kept up to date as its registers change,
    // since the machine asks after every run of instructions
    private int _pending = -1;

    /// <summary>A controller, its registers as they are before a program initializes it.</summary>
    /// <param name="vectorBase">The vector of its first line, until a program initializes it: 08h for the first, 70h for the second.</param>
    /// <param name="mask">Its mask register at the start.</param>
    /// <param name="second">For the first controller, the second, on its line 2.</param>
    public InterruptController(byte vectorBase = 0x08, byte mask = 0x00, InterruptController? second = null)
    {
        _vectorBase = vectorBase;
        _mask = mask;
        _second = second;
        if (second is not null)
        {
            second._first = this;
        }
    }

    private byte _request;
    private byte _inService;
    private byte _mask;
    private byte _vectorBase;
    private bool _readInService;

    // initialization: ICW1 to port 20h starts a sequence of two or three more words to port 21h
    private int _initializationStep;
    private bool _expectIcw4;

    /// <summary>A device raises one of the controller's lines, 0 to 7.</summary>
    public void Raise(int line)
    {
        _request |= (byte)(1 << line);
        Changed();
    }

    // its registers have changed: what it would give the processor, and so perhaps the first's
    private void Changed()
    {
        _pending = Pending();
        _first?.Changed();
    }

    /// <summary>The line the controller would give the processor now, or -1.</summary>
    private int Pending()
    {
        // the second controller's line is up while it has an interrupt to give
        int request = _request | ((_second?.HasInterrupt ?? false) ? 1 << Cascade : 0);
        int ready = request & ~_mask;
        for (int irq = 0; irq < 8; irq++)
        {
            int bit = 1 << irq;
            if ((_inService & bit) != 0)
            {
                // an IRQ in service blocks itself and everything of lower priority
                return -1;
            }

            if ((ready & bit) != 0)
            {
                return irq;
            }
        }

        return -1;
    }

    public bool HasInterrupt => _pending >= 0;

    /// <summary>
    /// The processor takes the interrupt: its vector, its IRQ now in service. On the cascade line,
    /// the second controller gives the vector, and its IRQ is in service there too.
    /// </summary>
    public int Acknowledge()
    {
        int irq = _pending;
        _request &= (byte)~(1 << irq);
        _inService |= (byte)(1 << irq);
        int vector = irq == Cascade && _second is not null ? _second.Acknowledge() : _vectorBase + irq;
        Changed();
        return vector;
    }

    /// <summary>End of interrupt for the highest-priority IRQ in service, as BIOS handlers send it.</summary>
    public void EndOfInterrupt()
    {
        for (int irq = 0; irq < 8; irq++)
        {
            if ((_inService & (1 << irq)) != 0)
            {
                _inService &= (byte)~(1 << irq);
                break;
            }
        }

        Changed();
    }

    /// <summary>Reads its command port (20h or A0h) or its data port (21h or A1h).</summary>
    public byte Read(int port)
    {
        if ((port & 1) != 0)
        {
            return _mask;
        }

        return _readInService ? _inService : _request;
    }

    /// <summary>Writes its command port (20h or A0h) or its data port (21h or A1h).</summary>
    public void Write(int port, byte value)
    {
        WriteRegister(port, value);
        Changed();
    }

    private void WriteRegister(int port, byte value)
    {
        if ((port & 1) == 0)
        {
            if ((value & 0x10) != 0)
            {
                // ICW1: start initializing; the mask is cleared
                _initializationStep = 2;
                _expectIcw4 = (value & 1) != 0;
                _mask = 0;
                _inService = 0;
            }
            else if ((value & 0x18) == 0x08)
            {
                // OCW3: which register port 20h reads
                if ((value & 2) != 0)
                {
                    _readInService = (value & 1) != 0;
                }
            }
            else if ((value & 0xE0) == 0x20)
            {
                EndOfInterrupt();
            }
            else if ((value & 0xE0) == 0x60)
            {
                // specific end of interrupt
                _inService &= (byte)~(1 << (value & 7));
            }

            return;
        }

        switch (_initializationStep)
        {
            case 2:
                _vectorBase = (byte)(value & 0xF8);
                _initializationStep = 3;
                break;
            case 3:
                // ICW3, the cascade; then ICW4 if ICW1 asked for it
                _initializationStep = _expectIcw4 ? 4 : 0;
                break;
            case 4:
                _initializationStep = 0;
                break;
            default:
                _mask = value;
                break;
        }
    }
}
