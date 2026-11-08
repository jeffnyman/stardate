using System.Security.Cryptography;

namespace Stardate.Core.Tests;

/// <summary>
/// Saved states. A state loaded into a fresh machine must carry on exactly as the machine did
/// from the moment it was saved: the same processor, memory and picture after the same keys.
/// Anything a device keeps that the state missed shows up as the runs drifting apart.
/// </summary>
public sealed class StateTests : IDisposable
{
    private readonly string _saves = Directory.CreateTempSubdirectory("stardate-saves").FullName;

    public void Dispose() => Directory.Delete(_saves, recursive: true);

    /// <summary>Saved at The Kobayashi Alternative's opening orders, and played on through a key and a command.</summary>
    [Fact]
    public void A_state_carries_on_a_game()
    {
        string game = TestData.Game("stka")!;
        var original = new Machine(game, _saves);
        original.Load("ST.EXE");
        original.Run(12 * original.InstructionsPerSecond);

        var state = new MemoryStream();
        original.SaveState(state);
        state.Position = 0;
        var restored = new Machine(game, _saves);
        restored.LoadState(state);

        Assert.Equal(Play(original), Play(restored));
    }

    [Fact]
    public void Something_else_is_not_a_state()
    {
        var machine = new Machine(_saves, _saves);
        Assert.Throws<InvalidDataException>(() => machine.LoadState(new MemoryStream([0, 1, 2, 3, 4, 5])));
        Assert.Throws<InvalidDataException>(() => machine.LoadState(new MemoryStream()));

        // a state cut short
        var state = new MemoryStream();
        machine.SaveState(state);
        var cut = new MemoryStream(state.ToArray()[..(int)(state.Length / 2)]);
        Assert.Throws<InvalidDataException>(() => machine.LoadState(cut));

        // and a whole one, into a machine as it was made
        state.Position = 0;
        new Machine(_saves, _saves).LoadState(state);
    }

    /// <summary>Plays on: the space bar, a command, and a few seconds; then a fingerprint of the processor, the screen and the picture.</summary>
    private static string Play(Machine machine)
    {
        Keys.Type(machine, " ");
        machine.Run(5 * machine.InstructionsPerSecond);
        Keys.Type(machine, "look\n");
        machine.Run(3 * machine.InstructionsPerSecond);

        var pixels = new uint[machine.DisplayWidth * machine.DisplayHeight];
        machine.Render(pixels);
        string picture = Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan())));
        var cpu = machine.Cpu;
        return $"{cpu.Cs:X4}:{cpu.Eip:X8} {cpu.Eax:X8} {cpu.Esp:X8} {machine.Time.Ticks}\n{string.Join('\n', machine.ScreenText())}\n{picture}";
    }
}
