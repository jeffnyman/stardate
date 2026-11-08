using Stardate.Core.Devices;

namespace Stardate.Core;

// Sound: the PC's speaker, which is all these games have.
public sealed partial class Machine
{
    /// <summary>The samples a second <see cref="ReadSamples"/> makes.</summary>
    public const int SampleRate = Speaker.SampleRate;

    private readonly Speaker _speaker = new();

    /// <summary>
    /// The speaker's sound up to the machine's time: 16-bit mono samples at
    /// <see cref="SampleRate"/>, from where the last read left off, as many as fit.
    /// </summary>
    /// <returns>How many samples were made.</returns>
    public int ReadSamples(Span<short> samples) => _speaker.Render(samples, TimerTicks);

    /// <summary>
    /// Lets the sound up to now go unheard: for a machine run without a player listening (or,
    /// say, at full speed), so the speaker's record of changes doesn't grow.
    /// </summary>
    public void SkipSamples() => _speaker.Skip(TimerTicks);

    private void UpdateSpeaker() => _speaker.Set(TimerTicks, (_portB & 2) != 0, (_portB & 1) != 0, _timer.Counter2);

    /// <summary>The BIOS's beep, for a bell character written to the screen.</summary>
    private void Beep() => _speaker.Beep(TimerTicks);
}
