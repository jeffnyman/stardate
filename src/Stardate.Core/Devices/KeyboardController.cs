namespace Stardate.Core.Devices;

/// <summary>
/// The AT's 8042 keyboard controller, from the program's side: the keyboard's scan codes arrive
/// at port 60h one at a time, each raising IRQ 1, and port 64h reports whether one is waiting.
/// Programs that take over the keyboard (games, mostly) read port 60h in their own IRQ 1 handler.
/// </summary>
/// <remarks>
/// The keyboard sends a make code when a key goes down and the same code with bit 7 set when it
/// comes up, with an E0h byte before each for the keys the AT's keyboard added (the arrows' own
/// block, the right Ctrl and Alt). The controller also drives the A20 gate, through its output
/// port: command D1h to port 64h, then the new port value to port 60h, bit 1 being A20.
/// </remarks>
internal sealed class KeyboardController
{
    private readonly Queue<byte> _waiting = new();
    private byte _output;
    private bool _writingOutputPort;

    /// <summary>Whether a byte waits in the output buffer for the program to read.</summary>
    public bool Full { get; private set; }

    public bool A20 { get; private set; }

    /// <summary>Raised when a byte enters the output buffer, for the machine to raise IRQ 1.</summary>
    public event Action? Interrupt;

    public void Send(byte code) => _waiting.Enqueue(code);

    /// <summary>
    /// Moves the next byte into the output buffer once the program has read the last: the keyboard
    /// sends a byte only when the controller can take it. Called by the machine between instructions.
    /// </summary>
    public void Update()
    {
        if (!Full && _waiting.TryDequeue(out byte code))
        {
            _output = code;
            Full = true;
            Interrupt?.Invoke();
        }
    }

    public byte In(int port)
    {
        if (port == 0x60)
        {
            Full = false;
            return _output;
        }

        // status: the output buffer's state, the system flag (the self-test has passed), and the
        // keyboard enabled
        return (byte)(0x14 | (Full ? 0x01 : 0x00));
    }

    public void Out(int port, byte value)
    {
        if (port == 0x64)
        {
            _writingOutputPort = value == 0xD1;
            switch (value)
            {
                case 0xDD: A20 = false; break;
                case 0xDF: A20 = true; break;
            }

            return;
        }

        if (_writingOutputPort)
        {
            A20 = (value & 0x02) != 0;
            _writingOutputPort = false;
        }
        else
        {
            // a command to the keyboard itself (its lights, its repeat rate): it acknowledges
            _waiting.Enqueue(0xFA);
        }
    }
}
