namespace Stardate.Driver;

/// <summary>
/// A route through a game: a step a line, as <c>name | script</c>, or <c>name | script | seconds</c>
/// to settle for longer than the usual second after it, with <c>#</c> starting a comment. Each step runs from the state the one before left, and leaves its own, so a route
/// can be picked up again at any step.
/// </summary>
internal sealed record Route(IReadOnlyList<RouteStep> Steps, double Pace)
{
    public static Route Read(string path)
    {
        var steps = new List<RouteStep>();
        int number = 0;
        double pace = 0;
        foreach (var raw in File.ReadLines(path))
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // @pace N: seconds to wait after every order, as a player reading would
            if (line.StartsWith("@pace", StringComparison.Ordinal))
            {
                pace = double.Parse(line[5..], System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            var parts = line.Split('|');
            if (parts.Length < 2)
            {
                throw new FormatException($"{path}:{number}: a step is \"name | script\".");
            }

            double settle = parts.Length > 2 && parts[2].Trim().Length > 0 ? double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 1;
            steps.Add(new RouteStep(parts[0].Trim(), parts[1].Trim(), settle, number));
        }

        return new Route(steps, pace);
    }

    /// <summary>
    /// Plays the route's steps, from a step (or the start) to a step (or the end), saving each
    /// step's state in the folder. True if every <c>{expect}</c> held.
    /// </summary>
    public bool Play(Session session, string statesFolder, string? from, string? to, Action<string> output)
    {
        int first = 0;
        if (from is not null)
        {
            first = Index(from);
        }

        int last = to is null ? Steps.Count - 1 : Index(to);
        string? previous = first == 0 ? null : StateFile(statesFolder, Steps[first - 1].Name);
        if (previous is not null && !File.Exists(previous))
        {
            throw new InvalidOperationException($"There's no state for the step before {from}, {Steps[first - 1].Name}: play the route up to it first.");
        }

        for (int i = first; i <= last; i++)
        {
            var step = Steps[i];
            output($"== {step.Name}");
            var driver = previous is null ? session.Start() : session.Resume(previous);
            driver.Pace = Pace;
            var result = Session.RunStep(driver, step.Script, output, step.Settle);
            previous = StateFile(statesFolder, step.Name);
            Session.Save(driver, previous);
            if (!result.Expected)
            {
                return false;
            }

            if (result.Ended)
            {
                break;
            }
        }

        return true;
    }

    public static string StateFile(string folder, string step) => Path.Combine(folder, step + ".state");

    private int Index(string name)
    {
        int index = Steps.ToList().FindIndex(s => s.Name == name);
        return index >= 0 ? index : throw new ArgumentException($"The route has no step {name}.");
    }
}

internal sealed record RouteStep(string Name, string Script, double Settle, int Line);
