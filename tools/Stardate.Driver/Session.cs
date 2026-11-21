using Stardate.Core;

namespace Stardate.Driver;

/// <summary>
/// One game's machines, all alike: the game's folder, where its writes go, its speed, and its
/// clock's start, which is fixed, so the game takes the same chances from the clock every time.
/// </summary>
internal sealed class Session(GameProfile profile, string gameFolder, string saveFolder)
{
    /// <summary>The clock's start, unless told otherwise: the 1st of March, 1987, at nine in the morning.</summary>
    public static readonly DateTime DefaultStart = new(1987, 3, 1, 9, 0, 0);

    public GameProfile Profile => profile;

    public DateTime StartTime { get; init; } = DefaultStart;

    public int? InstructionsPerSecond { get; init; }

    public bool StopAtPauses { get; init; }

    /// <summary>A new game, from its program.</summary>
    public Driver Start()
    {
        var driver = Create();
        driver.Machine.Load(profile.Program);
        return driver;
    }

    /// <summary>A game from a saved state.</summary>
    public Driver Resume(string stateFile)
    {
        var driver = Create();
        using var stream = File.OpenRead(stateFile);
        driver.Machine.LoadState(stream);
        return driver;
    }

    public static void Save(Driver driver, string stateFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stateFile))!);
        using var stream = File.Create(stateFile);
        driver.Machine.SaveState(stream);
    }

    private Driver Create()
    {
        Directory.CreateDirectory(saveFolder);
        var machine = new Machine(gameFolder, saveFolder) { StartTime = StartTime };
        if (InstructionsPerSecond is { } ips)
        {
            machine.InstructionsPerSecond = ips;
        }

        return new Driver(machine, profile) { StopAtPauses = StopAtPauses };
    }

    /// <summary>
    /// Runs a script as a step: the script, then some settling, a second unless told otherwise
    /// (a route's timing depends on it). What the game said is written out; the game's end, too.
    /// </summary>
    public static StepResult RunStep(Driver driver, string script, Action<string> output, double settle = 1)
    {
        driver.Output = output;
        string? failure = null;
        try
        {
            new Script(driver).Run(script);
        }
        catch (Script.ExpectationException e)
        {
            failure = e.Message;
        }

        driver.RunSeconds(settle);
        driver.Run(1);
        driver.Snapshot();
        foreach (var line in driver.Profile.Said(driver.Snapshots))
        {
            output("  " + line);
        }

        if (driver.Machine.Exited)
        {
            output($"  [the game ended, with code {driver.Machine.ExitCode}]");
        }

        if (failure is not null)
        {
            output("  [*** " + failure + "]");
        }

        return new StepResult(failure is null, driver.Machine.Exited);
    }
}

internal readonly record struct StepResult(bool Expected, bool Ended);
