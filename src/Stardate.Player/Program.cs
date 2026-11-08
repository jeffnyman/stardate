using System.Reflection;
using Stardate.Core;
using Stardate.Player.Sdl;

namespace Stardate.Player;

internal static class Program
{
    // exit codes: done, not found, a mistake on the command line, and the machine or window failed
    private const int Done = 0, NotFound = 1, BadCommandLine = 2, CannotRun = 3;

    /// <summary>A mistake on the command line, said in one line.</summary>
    private sealed class UsageException(string message) : Exception(message);

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (UsageException ex)
        {
            Error(ex.Message);
            Console.Error.WriteLine("Run 'stardate --help' for more.");
            return BadCommandLine;
        }
        catch (FileNotFoundException ex)
        {
            Error(ex.Message);
            return NotFound;
        }
        catch (Exception ex) when (ex is SdlException or InvalidDataException or IOException)
        {
            Error(ex.Message);
            return CannotRun;
        }
    }

    /// <summary>The command line, run: what <see cref="Main"/> does, without its error reporting.</summary>
    internal static int Run(string[] args)
    {
        string? gameName = null;
        string arguments = "";
        string? saves = null;
        string? logPath = null;
        string? screenshot = null;
        string? statePath = null;
        bool resume = false;
        string settingsPath = Settings.DefaultPath;
        double? seconds = null;
        bool headless = false;
        bool list = false;
        bool remember = false;
        bool forget = false;

        // the options a profile can keep, as given here: they win over the profile for this run
        var given = new Profile();

        int i = 0;

        // the value after an option, which must be there and not be another option
        string Next(string option, string what)
            => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : throw new UsageException($"{option} needs {what}");

        int Number(string option, string what, int min, int max)
        {
            string value = Next(option, what);
            return int.TryParse(value, out int number) && number >= min && number <= max
                ? number
                : throw new UsageException($"{option} needs {what}, not '{value}'");
        }

        for (; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h" or "-?":
                    PrintHelp();
                    return Done;
                case "--version":
                    Console.WriteLine($"stardate {Version()}");
                    return Done;
                case "--list":
                    list = true;
                    break;
                case "--settings":
                    settingsPath = Path.GetFullPath(Next("--settings", "a file name"));
                    break;
                case "--remember":
                    remember = true;
                    break;
                case "--forget":
                    forget = true;
                    break;
                case "--args":
                    arguments = i + 1 < args.Length ? args[++i] : throw new UsageException("--args needs the program's arguments");
                    break;
                case "--saves":
                    saves = Next("--saves", "a folder");
                    break;
                case "--cycles":
                    given.Cycles = Number("--cycles", "instructions a millisecond, as in 1000", 100, 1_000_000);
                    break;
                case "--scale":
                    given.Scale = Number("--scale", "a scale from 1 to 4", 1, 4);
                    break;
                case "--fullscreen":
                    given.Fullscreen = true;
                    break;
                case "--windowed":
                    given.Fullscreen = false;
                    break;
                case "--square-pixels" or "--no-square-pixels":
                    given.SquarePixels = args[i] == "--square-pixels";
                    break;
                case "--sound" or "--no-sound":
                    given.Sound = args[i] == "--sound";
                    break;
                case "--cga-font" or "--no-cga-font":
                    given.CgaFont = args[i] == "--cga-font";
                    break;
                case "--state":
                    statePath = Path.GetFullPath(Next("--state", "a file name"));
                    break;
                case "--resume":
                    resume = true;
                    break;
                case "--log":
                    logPath = Next("--log", "a file name");
                    break;
                case "--seconds":
                    seconds = Number("--seconds", "a number of seconds", 1, int.MaxValue);
                    break;
                case "--screenshot":
                    screenshot = Next("--screenshot", "a file name");
                    break;
                case "--headless":
                    headless = true;
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new UsageException($"unknown option {args[i]}");
                    }

                    gameName = gameName is null ? args[i] : throw new UsageException("one game at a time");
                    break;
            }
        }

        var settings = Settings.Load(settingsPath);
        if (list)
        {
            List(settings);
            return Done;
        }

        if (headless && seconds is null)
        {
            throw new UsageException("--headless needs --seconds, or it would never end");
        }

        Game game;
        if (gameName is null)
        {
            if (headless)
            {
                throw new UsageException("--headless needs a game");
            }

            // no game named: the menu
            if (SdlMenu.Choose(settings) is not { } chosen)
            {
                return Done;
            }

            game = chosen;
        }
        else
        {
            game = Games.Named(gameName) ?? throw new UsageException($"no game called '{gameName}': the games are {Games.Names}");
        }

        string program = Games.ProgramOf(game);

        // the game's profile, with what was given here over it
        bool changed = false;
        if (forget && settings.Profiles.Remove(game.Name))
        {
            changed = true;
        }

        var profile = new Profile();
        profile.Take(settings.ProfileOf(game));
        if (remember && !given.IsEmpty)
        {
            var kept = settings.ProfileOf(game);
            kept.Take(given);
            settings.Profiles[game.Name] = kept;
            changed = true;
        }

        profile.Take(given);
        if (changed)
        {
            settings.Save();
        }

        bool sound = profile.Sound ?? true;
        saves ??= Games.SaveFolder(game);
        Directory.CreateDirectory(saves);

        using var log = logPath is null ? null : new StreamWriter(logPath);
        var machine = new Machine(game.Folder, saves)
        {
            InstructionsPerSecond = (profile.Cycles ?? game.Cycles) * 1000,
            Log = log is null ? null : log.WriteLine,
            CgaFont = profile.CgaFont ?? false,
        };

        machine.Load(program, arguments);
        statePath ??= Games.StateFile(game);
        if (resume)
        {
            // on from where Ctrl+F5 last saved, the machine as it was then
            if (!File.Exists(statePath))
            {
                throw new FileNotFoundException($"no state saved at {statePath}: Ctrl+F5 saves one while playing");
            }

            using var state = File.OpenRead(statePath);
            machine.LoadState(state);
            Console.WriteLine($"stardate: on from the state saved {File.GetLastWriteTime(statePath):g}");
        }

        Console.WriteLine($"stardate: {game.Title}, {program} at {machine.InstructionsPerSecond / 1000} cycles; saves in {saves}");

        bool running;
        try
        {
            if (headless)
            {
                running = RunHeadless(machine, seconds!.Value);
            }
            else
            {
                var options = new PlayerOptions
                {
                    Title = $"Stardate - {game.Title}",
                    Scale = profile.Scale ?? 2,
                    Fullscreen = profile.Fullscreen ?? false,
                    SquarePixels = profile.SquarePixels ?? false,
                    Sound = sound,
                    StatePath = statePath,
                };

                using var player = new SdlPlayer(machine, options);
                running = player.Run(seconds);
            }
        }
        catch (NotSupportedException ex)
        {
            // something the machine doesn't do yet: where the game was, for the log and the notes
            var cpu = machine.Cpu;
            Error($"{ex.Message} At {cpu.Cs:X4}:{cpu.Eip:X4}, {machine.Time.TotalSeconds:F1} s in.");
            return CannotRun;
        }

        if (screenshot is not null)
        {
            Screenshot.WritePng(machine, screenshot);
        }

        if (machine.Exited)
        {
            Console.WriteLine($"stardate: {program} ended with code {machine.ExitCode}");
            return machine.ExitCode == 0 ? Done : CannotRun;
        }

        if (!running)
        {
            Error($"the processor stopped, halted with interrupts off, at {machine.Cpu.Cs:X4}:{machine.Cpu.Eip:X4}");
            return CannotRun;
        }

        return Done;
    }

    /// <summary>
    /// Runs the machine with no window for the emulated seconds given, a second at a time, with
    /// the sound let go unheard as it goes. Returns whether the program is still running.
    /// </summary>
    private static bool RunHeadless(Machine machine, double seconds)
    {
        long due = (long)(seconds * machine.InstructionsPerSecond);
        long run = 0;
        bool running = true;
        while (running && run < due)
        {
            long slice = Math.Min(machine.InstructionsPerSecond, due - run);
            running = machine.Run(slice);
            run += slice;
            machine.SkipSamples();
        }

        return running;
    }

    /// <summary>What the player has: each game, whether it's there, and how it's played.</summary>
    private static void List(Settings settings)
    {
        Console.WriteLine($"settings: {settings.Path}");
        Console.WriteLine($"games: {Games.Root}");
        foreach (var game in Games.Known)
        {
            Console.WriteLine();
            string missing = Games.IsThere(game) ? "" : $"  (missing: no {game.Program} in {game.Folder})";
            Console.WriteLine($"{game.Name}  {game.Title}{missing}");
            Console.WriteLine($"  played with {settings.ProfileOf(game)}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            stardate {Version()}: plays Simon & Schuster's three Star Trek text adventures, and only that.

            usage: stardate [game] [options]

              [game]             a game's short name: stfc (Star Trek: First Contact), stka (Star
                                 Trek: The Kobayashi Alternative) or stpp (Star Trek: The
                                 Promethean Prophecy). All three come with the player, in the
                                 folder games beside it. With no game, a menu in a window lists
                                 them.

            options:
              --list             the games, and how each is played
              --remember         keep this run's options below as the game's own
              --forget           go back to the defaults for the game
              --settings <file>  the settings file (by default stardate.json in the user's
                                 application data, beside the saves)
              --args "<text>"    the program's command line arguments
              --saves <folder>   where the game's writes go, saved games included (by default a
                                 folder of its own in the user's application data); the game's
                                 folder is never written
              --cycles <n>       instructions a millisecond, as DOSBox counts cycles: 1000 by
                                 default, as the games' DOSBox setups ran them
              --scale <n>        the window's first size, n times the 640-wide picture (2), made
                                 smaller if the screen hasn't room
              --fullscreen       start in full screen (--windowed: in a window)
              --square-pixels    square pixels, not the 4:3 picture a monitor showed
                                 (--no-square-pixels: 4:3)
              --cga-font         the CGA's 8 by 8 font, as on the PCs the games were written for,
                                 rather than the VGA's 8 by 16 (--no-cga-font: the VGA's)
              --no-sound         no sound from the PC speaker (--sound: back on)
              --resume           start from the state Ctrl+F5 last saved
              --state <file>     the file Ctrl+F5 saves the state to, Ctrl+F9 and --resume load
                                 it from (by default one for each game, in a folder states
                                 beside the saves)
              --log <file>       write every DOS and BIOS call, and unanswered port, to a file
              --seconds <n>      stop after n seconds of the game's time
              --screenshot <f>   save the picture as a PNG when stopping
              --headless         no window: run --seconds seconds and stop

            playing:
              the keyboard is the PC's, key for key
              F11 full screen, Shift+F11 pixel shape, F12 full speed on and off, Pause pauses
              Ctrl+F5 saves the whole machine's state, Ctrl+F9 loads it back
            """);
    }

    private static string Version()
        => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    private static void Error(string message) => Console.Error.WriteLine($"stardate: {message}");
}
