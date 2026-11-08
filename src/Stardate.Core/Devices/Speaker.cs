namespace Stardate.Core.Devices;

/// <summary>
/// The PC's speaker, driven by bits 0 and 1 of port 61h. Bit 1 lets the sound through; bit 0
/// gates the timer's counter 2, whose square wave then plays a tone. With bit 0 off, a program
/// can toggle bit 1 itself, as the games of the time often did, to make sounds the timer can't.
/// </summary>
/// <remarks>
/// The speaker keeps each change of those bits and of counter 2, with its time in timer ticks,
/// and turns them into samples when the player asks for them, so the sound follows the
/// machine's time exactly however unevenly the machine is run.
/// </remarks>
internal sealed class Speaker
{
    /// <summary>The samples a second the speaker makes.</summary>
    public const int SampleRate = 44_100;

    private const double TicksPerSample = IntervalTimer.Frequency / SampleRate;

    // each sample is the average of this many points across its time, which keeps the sound of
    // fast toggling from aliasing too badly
    private const int Oversampling = 8;

    private const double Volume = 6000;

    /// <summary>The speaker's state from a moment on.</summary>
    private struct Change
    {
        public long Tick;
        public bool Data;
        public bool Gate;
        public int Reload;
        public int Mode;
        public long Start;
    }

    private readonly List<Change> _changes = [new() { Reload = 0x10000, Mode = 3 }];

    // the tick the samples made so far reach, and the high-pass filter's memory, which takes
    // away the steady level a speaker held on would leave
    private double _renderedTick;
    private double _lastInput;
    private double _lastOutput;

    /// <summary>
    /// The speaker's bits or counter 2 have been set, at a time: a change from then on. A change
    /// still to come, the end of a beep, is overtaken by it.
    /// </summary>
    public void Set(long tick, bool data, bool gate, (int Reload, int Mode, long Start) counter)
    {
        _changes.RemoveAll(change => change.Tick > tick);
        var last = _changes[^1];
        if (last.Data == data && last.Gate == gate && last.Reload == counter.Reload && last.Mode == counter.Mode && last.Start == counter.Start)
        {
            return;
        }

        _changes.Add(new Change { Tick = tick, Data = data, Gate = gate, Reload = counter.Reload, Mode = counter.Mode, Start = counter.Start });
    }

    /// <summary>
    /// A beep, as the BIOS sounds for a bell character: a tone of about 900 Hz for a quarter of
    /// a second, after which the speaker goes back as it was.
    /// </summary>
    public void Beep(long tick)
    {
        const int reload = 1331; // 1,193,182 / 896 Hz, as IBM's BIOS has it
        var before = _changes[^1];
        _changes.RemoveAll(change => change.Tick > tick);
        _changes.Add(new Change { Tick = tick, Data = true, Gate = true, Reload = reload, Mode = 3, Start = tick });
        _changes.Add(before with { Tick = tick + (long)(IntervalTimer.Frequency / 4) });
    }

    /// <summary>The samples there are to make up to a time, in ticks.</summary>
    public int Available(long tick) => (int)Math.Max(0, (tick - _renderedTick) / TicksPerSample);

    /// <summary>Makes samples onward from where the last ones ended, as many as fit, up to the time given.</summary>
    /// <returns>How many it made.</returns>
    public int Render(Span<short> samples, long upTo)
    {
        int count = Math.Min(samples.Length, Available(upTo));
        int index = 0;
        for (int i = 0; i < count; i++)
        {
            double sum = 0;
            for (int k = 0; k < Oversampling; k++)
            {
                long tick = (long)(_renderedTick + ((k + 0.5) * TicksPerSample / Oversampling));
                while (index + 1 < _changes.Count && _changes[index + 1].Tick <= tick)
                {
                    index++;
                }

                sum += Level(_changes[index], tick) ? 1 : 0;
            }

            double input = sum / Oversampling;
            _lastOutput = input - _lastInput + (0.995 * _lastOutput);
            _lastInput = input;
            samples[i] = (short)Math.Clamp(_lastOutput * Volume, short.MinValue, short.MaxValue);
            _renderedTick += TicksPerSample;
        }

        // the changes before the one in force are done with
        long now = (long)_renderedTick;
        int done = _changes.FindLastIndex(change => change.Tick <= now);
        if (done > 0)
        {
            _changes.RemoveRange(0, done);
        }

        return count;
    }

    /// <summary>Skips the samples up to a time, unmade: for a machine with nobody listening, so the changes don't pile up.</summary>
    public void Skip(long upTo)
    {
        if (upTo <= _renderedTick)
        {
            return;
        }

        _renderedTick = upTo;
        int done = _changes.FindLastIndex(change => change.Tick <= upTo);
        if (done > 0)
        {
            _changes.RemoveRange(0, done);
        }
    }

    private static bool Level(in Change change, long tick)
    {
        if (!change.Data)
        {
            return false;
        }

        if (!change.Gate || change.Mode != 3)
        {
            // held high, or a counter mode that doesn't make a tone: no sound but its edges
            return true;
        }

        long elapsed = Math.Max(0, tick - change.Start);
        return elapsed % change.Reload < change.Reload / 2;
    }
}
