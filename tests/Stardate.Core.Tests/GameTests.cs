namespace Stardate.Core.Tests;

/// <summary>
/// The three games, from the repository's <c>games</c>: each plays from its start to the point
/// where the captain types orders, by the keys a player would press.
/// </summary>
public sealed class GameTests : IDisposable
{
    private readonly string _saves = Directory.CreateTempSubdirectory("stardate-saves").FullName;
    private readonly List<string> _log = [];

    public void Dispose() => Directory.Delete(_saves, recursive: true);

    [Fact]
    public void First_contact_shows_its_title_and_reaches_the_bridge()
    {
        var machine = Start("stfc", "ST.EXE");
        RunUntil(machine, 30, () => Shows(machine, "Press any key to continue"));
        Keys.Type(machine, " ");
        RunUntil(machine, 10, () => machine.VideoMode == 4);
        Assert.Equal(4, machine.VideoMode);

        // the title picture, then the publisher's credit, on a timer, then the bridge
        PressUntil(machine, "┌ Kirk", 6, 30);
        Assert.True(Shows(machine, "┌ U.S.S. Enterprise"), Screen(machine));
    }

    [Fact]
    public void The_kobayashi_alternative_reaches_the_bridge_and_takes_an_order()
    {
        var machine = Start("stka", "ST.EXE");
        RunUntil(machine, 30, () => Shows(machine, "DISAPPEARANCE OF USS HEINLEIN"));
        PressUntil(machine, "Your orders?", 6, 10);
        Keys.Type(machine, "look");
        RunUntil(machine, 5, () => Shows(machine, "►look"));
        Assert.True(Shows(machine, "►look"), Screen(machine));
    }

    [Fact]
    public void The_promethean_prophecy_starts_from_its_disk_image_and_reaches_the_bridge()
    {
        var machine = Start("stpp", "RUN.COM");
        RunUntil(machine, 30, () => Shows(machine, "Captain's Log"));
        PressUntil(machine, "Moves:", 6, 10);
        Assert.True(Shows(machine, "Bridge"), Screen(machine));
    }

    /// <summary>Presses the space bar, waiting after each press, until the text shows; a player going through the opening screens.</summary>
    private void PressUntil(Machine machine, string text, int presses, double seconds)
    {
        for (int i = 0; i < presses && !Shows(machine, text); i++)
        {
            Keys.Type(machine, " ");
            RunUntil(machine, seconds, () => Shows(machine, text));
        }

        Assert.True(Shows(machine, text), $"No \"{text}\" after {presses} presses:\n{Screen(machine)}");
    }

    private Machine Start(string game, string program)
    {
        var machine = new Machine(TestData.Game(game)!, _saves) { Log = _log.Add };
        machine.Load(program);
        return machine;
    }

    /// <summary>Runs, a tenth of a second at a time, until the condition holds or the seconds are up.</summary>
    private void RunUntil(Machine machine, double seconds, Func<bool> done)
    {
        for (double t = 0; t < seconds && !done(); t += 0.1)
        {
            Assert.True(machine.Run(machine.InstructionsPerSecond / 10), "The game ended.\n" + string.Join('\n', _log.TakeLast(20)));
        }
    }

    private static bool Shows(Machine machine, string text) => machine.ScreenText().Any(line => line.Contains(text, StringComparison.Ordinal));

    private static string Screen(Machine machine) => string.Join('\n', machine.ScreenText());
}
