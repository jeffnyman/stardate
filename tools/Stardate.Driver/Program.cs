using System.Globalization;

namespace Stardate.Driver;

/// <summary>
/// stardate-driver: plays Stardate's games headless, from scripts of keys and commands, and looks
/// inside them as they play.
/// </summary>
internal static class Program
{
    private const string Usage = """
        stardate-driver: plays Stardate's games headless, by script

        usage:
          stardate-driver play <game> <route file> [options]
              plays a route, a step a line, saving each step's state; --from and --to pick
              up and stop at named steps
          stardate-driver run <game> <state|-> <state out|-> "<script>" [options]
              runs one script, from a saved state or (-) a new game, and saves where it ends

        games: stka or stpp

        options:
          --states <folder>   where a route's states go (default: driver-states/<game>)
          --from <step>       play a route from this step, from the state the step before left
          --to <step>         stop after this step
          --games <folder>    the games' folder (default: the repository's games, found above here)
          --saves <folder>    where the games' own writes go (default: in the states folder)
          --cycles <n>        instructions a millisecond (default: the machine's, 240)
          --start <time>      the machine clock's start (default: 1987-03-01T09:00:00)
          --stop-at-pauses    leave a "press a key" pause waiting, rather than pressing through
          --screen            after a run, print the whole screen
        """;

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine("stardate-driver: " + e.Message);
            return 2;
        }
    }

    internal static int Run(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string?>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--stop-at-pauses" or "--screen")
            {
                options[a] = null;
            }
            else if (a.StartsWith("--", StringComparison.Ordinal))
            {
                options[a] = i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");
            }
            else
            {
                positional.Add(a);
            }
        }

        if (positional.Count < 2 || positional[0] is not ("play" or "run") || args.Contains("--help"))
        {
            Console.WriteLine(Usage);
            return positional.Count == 0 || args.Contains("--help") ? 0 : 2;
        }

        var profile = GameProfile.Named(positional[1]) ?? throw new ArgumentException($"There's no game {positional[1]}: {GameProfile.Names}.");
        string games = options.GetValueOrDefault("--games") ?? FindGames() ?? throw new ArgumentException("The games' folder isn't found: give it with --games.");
        string states = options.GetValueOrDefault("--states") ?? Path.Combine("driver-states", profile.Name);
        var session = new Session(profile, Path.Combine(games, profile.Name), options.GetValueOrDefault("--saves") ?? Path.Combine(states, "saves"))
        {
            StartTime = options.GetValueOrDefault("--start") is { } start ? DateTime.Parse(start, CultureInfo.InvariantCulture) : Session.DefaultStart,
            InstructionsPerSecond = options.GetValueOrDefault("--cycles") is { } cycles ? int.Parse(cycles, CultureInfo.InvariantCulture) * 1000 : null,
            StopAtPauses = options.ContainsKey("--stop-at-pauses"),
        };

        if (positional[0] == "play")
        {
            if (positional.Count != 3)
            {
                throw new ArgumentException("play takes a game and a route file.");
            }

            var route = Route.Read(positional[2]);
            return route.Play(session, states, options.GetValueOrDefault("--from"), options.GetValueOrDefault("--to"), Console.WriteLine) ? 0 : 3;
        }

        if (positional.Count != 5)
        {
            throw new ArgumentException("run takes a game, a state (or -), a state to save (or -), and a script.");
        }

        var driver = positional[2] == "-" ? session.Start() : session.Resume(positional[2]);
        var result = Session.RunStep(driver, positional[4], Console.WriteLine);
        if (positional[3] != "-")
        {
            Session.Save(driver, positional[3]);
        }

        if (options.ContainsKey("--screen"))
        {
            foreach (var line in driver.ScreenText())
            {
                Console.WriteLine(line.TrimEnd());
            }
        }

        return result.Expected ? 0 : 3;
    }

    // the repository's games folder: one with stka and stpp in it, here or above
    internal static string? FindGames()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "games");
                if (Directory.Exists(Path.Combine(candidate, "stka")) && Directory.Exists(Path.Combine(candidate, "stpp")))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
