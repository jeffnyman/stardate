namespace Stardate.Core.Tests;

/// <summary>The PC speaker, with small programs.</summary>
public sealed class SoundTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("stardate-game").FullName;
    private readonly string _saves = Directory.CreateTempSubdirectory("stardate-saves").FullName;

    public void Dispose()
    {
        Directory.Delete(_game, recursive: true);
        Directory.Delete(_saves, recursive: true);
    }

    [Fact]
    public void Counter_2_plays_its_tone_through_the_speaker()
    {
        //   mov al, 0B6h; out 43h, al           counter 2, both bytes, a square wave
        //   mov ax, 1193; out 42h, al; mov al, ah; out 42h, al    1,000 Hz
        //   in al, 61h; or al, 3; out 61h, al   the gate and the speaker on
        //   wait: jmp wait
        var machine = Start(
        [
            0xB0, 0xB6, 0xE6, 0x43,
            0xB8, 0xA9, 0x04, 0xE6, 0x42, 0x88, 0xE0, 0xE6, 0x42,
            0xE4, 0x61, 0x0C, 0x03, 0xE6, 0x61,
            0xEB, 0xFE,
        ]);

        machine.Run(machine.InstructionsPerSecond / 2);
        var samples = new short[Machine.SampleRate];
        int count = machine.ReadSamples(samples);
        Assert.InRange(count, (Machine.SampleRate / 2) - 10, Machine.SampleRate / 2);

        // a thousand cycles a second: a rising edge each 44 samples or so, after the first few
        int rising = 0;
        for (int i = 1000; i < count; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                rising++;
            }
        }

        double seconds = (count - 1000) / (double)Machine.SampleRate;
        Assert.InRange(rising / seconds, 990, 1010);
    }

    [Fact]
    public void The_speaker_is_silent_until_a_program_turns_it_on()
    {
        var machine = Start([0xEB, 0xFE]);
        machine.Run(machine.InstructionsPerSecond / 10);
        var samples = new short[Machine.SampleRate];
        int count = machine.ReadSamples(samples);
        Assert.True(count > 0);
        Assert.All(samples[..count], sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void A_bell_written_to_the_screen_beeps()
    {
        // mov ax, 0E07h; int 10h; wait: jmp wait
        var machine = Start([0xB8, 0x07, 0x0E, 0xCD, 0x10, 0xEB, 0xFE]);
        machine.Run(machine.InstructionsPerSecond / 10);
        var samples = new short[Machine.SampleRate];
        int count = machine.ReadSamples(samples);
        Assert.Contains(samples[..count], sample => Math.Abs((int)sample) > 1000);
    }

    private Machine Start(byte[] code)
    {
        File.WriteAllBytes(Path.Combine(_game, "TEST.EXE"), DosTests.Exe(code));
        var machine = new Machine(_game, _saves);
        machine.Load("TEST.EXE");
        return machine;
    }
}
