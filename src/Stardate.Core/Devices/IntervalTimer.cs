namespace Stardate.Core.Devices;

/// <summary>
/// The Intel 8253 programmable interval timer: three 16-bit counters counting down at
/// 1,193,182 Hz, a third of the original PC's 3.58 MHz color-burst crystal. Counter 0 raises IRQ 0,
/// 18.2 times a second unless a program reprograms it; counter 2 drives the PC speaker; counter 1
/// refreshed memory and is left alone.
/// </summary>
/// <remarks>
/// Programs also read the counters to measure short times, latching one with a command to port
/// 43h and then reading its two bytes from its port. The counters here aren't stepped: each
/// remembers when it was loaded, and its count at any moment is worked out from the time.
/// </remarks>
internal sealed class IntervalTimer
{
    public const double Frequency = 1_193_182;

    private readonly Counter[] _counters = [new(), new(), new()];

    /// <summary>The time in timer ticks, set by the machine before each access.</summary>
    public long Now { get; set; }

    /// <summary>Whether counter 2 counts: bit 0 of port 61h.</summary>
    public bool Gate2
    {
        get => _counters[2].Gate;
        set
        {
            var counter = _counters[2];
            if (value && !counter.Gate)
            {
                // a rising gate starts the count again
                counter.Start = Now;
            }

            counter.Gate = value;
        }
    }

    private sealed class Counter
    {
        public int Reload = 0x10000;
        public int Mode = 3;
        public int Access = 3;
        public long Start;
        public bool Gate = true;

        // reading and writing the two bytes, low first
        public bool ReadHigh;
        public bool WriteHigh;
        public int? Latched;
        public int LowWritten;

        /// <summary>The ticks since loading, or 0 while the gate holds the count.</summary>
        public long Elapsed(long now) => Gate ? Math.Max(0, now - Start) : 0;

        public int Count(long now)
        {
            long elapsed = Elapsed(now);
            return Mode switch
            {
                // a square wave counts down by two, twice in each period
                3 => (int)((Reload - ((2 * elapsed) % Reload)) & 0xFFFF),
                0 or 1 => (int)((Reload - elapsed) & 0xFFFF),
                _ => (int)((Reload - (elapsed % Reload)) & 0xFFFF),
            };
        }

        public bool Output(long now)
        {
            long elapsed = Elapsed(now);
            return Mode switch
            {
                0 or 1 => elapsed >= Reload,
                3 => elapsed % Reload < Reload / 2,
                _ => true,
            };
        }
    }

    /// <summary>Counter 0's period in ticks: the time between IRQ 0s.</summary>
    public int Period0 => _counters[0].Reload;

    /// <summary>When counter 0 was last loaded, from which its IRQ 0s are timed.</summary>
    public long Start0 => _counters[0].Start;

    /// <summary>Whether counter 0 counts again after reaching zero; in mode 0 it interrupts once.</summary>
    public bool Periodic0 => _counters[0].Mode is 2 or 3;

    public bool Output2 => _counters[2].Output(Now);

    /// <summary>Counter 2 as it is set: what the speaker needs to know to play it.</summary>
    public (int Reload, int Mode, long Start) Counter2 => (_counters[2].Reload, _counters[2].Mode, _counters[2].Start);

    /// <summary>Raised when counter 0 is loaded, so the machine can time IRQ 0 from then.</summary>
    public event Action? Reloaded0;

    public byte Read(int port)
    {
        if (port == 0x43)
        {
            return 0xFF;
        }

        var counter = _counters[port - 0x40];
        int value = counter.Latched ?? counter.Count(Now);
        byte result;
        switch (counter.Access)
        {
            case 1: result = (byte)value; counter.Latched = null; break;
            case 2: result = (byte)(value >> 8); counter.Latched = null; break;
            default:
                result = counter.ReadHigh ? (byte)(value >> 8) : (byte)value;
                if (counter.ReadHigh)
                {
                    counter.Latched = null;
                }

                counter.ReadHigh = !counter.ReadHigh;
                break;
        }

        return result;
    }

    public void Write(int port, byte value)
    {
        if (port == 0x43)
        {
            int channel = value >> 6;
            if (channel == 3)
            {
                return; // the 8254's read-back command, which the 8253 doesn't have
            }

            var counter = _counters[channel];
            int access = (value >> 4) & 3;
            if (access == 0)
            {
                // latch the count, to read it without it changing between the two bytes
                counter.Latched ??= counter.Count(Now);
                return;
            }

            counter.Access = access;
            counter.Mode = (value >> 1) & 7;
            if (counter.Mode > 5)
            {
                counter.Mode -= 4;
            }

            counter.ReadHigh = false;
            counter.WriteHigh = false;
            counter.Latched = null;
            return;
        }

        var target = _counters[port - 0x40];
        switch (target.Access)
        {
            case 1: Load(port - 0x40, value); break;
            case 2: Load(port - 0x40, value << 8); break;
            default:
                if (!target.WriteHigh)
                {
                    target.LowWritten = value;
                    target.WriteHigh = true;
                }
                else
                {
                    target.WriteHigh = false;
                    Load(port - 0x40, target.LowWritten | (value << 8));
                }

                break;
        }
    }

    private void Load(int channel, int count)
    {
        var counter = _counters[channel];

        // a count of 0 means 65536
        counter.Reload = count == 0 ? 0x10000 : count;
        counter.Start = Now;
        if (channel == 0)
        {
            Reloaded0?.Invoke();
        }
    }
}
