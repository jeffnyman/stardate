using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Stardate.Driver;

/// <summary>
/// A script of keys and commands for the driver: plain text is typed; anything in braces is a
/// key or a command, as <c>{enter}</c>, <c>{say:Spock, follow me}</c> or
/// <c>{repeat:20:{say:Wait}{if:lies!:{break}}}</c>. Braces nest, so a command can take a script.
/// The commands are listed in the tool's README.
/// </summary>
internal sealed partial class Script(Driver driver)
{
    private readonly HashSet<string> _marks = new(StringComparer.Ordinal);

    // thrown by {break}, and caught by the {repeat} around it
    private sealed class BreakException : Exception;

    /// <summary>Thrown by <c>{expect}</c> when the game isn't where the script expects it to be.</summary>
    public sealed class ExpectationException(string message) : Exception(message);

    /// <summary>Runs a script. A <c>{break}</c> outside any <c>{repeat}</c> ends it.</summary>
    public void Run(string script)
    {
        try
        {
            Execute(script);
        }
        catch (BreakException)
        {
        }
    }

    private void Execute(string script)
    {
        foreach (var (isCommand, text) in Parse(script))
        {
            // once the game has ended, only expectations are left to check, against its last screen
            if (driver.Machine.Exited && !(isCommand && text.StartsWith("expect:", StringComparison.Ordinal)))
            {
                continue;
            }

            if (isCommand)
            {
                Command(text);
            }
            else
            {
                driver.Type(text);
            }
        }
    }

    /// <summary>Splits a script into text and commands, the commands with their outer braces taken off.</summary>
    public static IEnumerable<(bool IsCommand, string Text)> Parse(string script)
    {
        var text = new StringBuilder();
        for (int i = 0; i < script.Length; i++)
        {
            if (script[i] != '{')
            {
                text.Append(script[i]);
                continue;
            }

            if (text.Length > 0)
            {
                yield return (false, text.ToString());
                text.Clear();
            }

            int depth = 0, start = i;
            for (; i < script.Length; i++)
            {
                if (script[i] == '{')
                {
                    depth++;
                }
                else if (script[i] == '}' && --depth == 0)
                {
                    break;
                }
            }

            if (depth != 0)
            {
                throw new FormatException($"A brace isn't closed: {script[start..]}");
            }

            yield return (true, script[(start + 1)..i]);
        }

        if (text.Length > 0)
        {
            yield return (false, text.ToString());
        }
    }

    private void Command(string command)
    {
        int colon = command.IndexOf(':');
        string name = colon < 0 ? command : command[..colon];
        string args = colon < 0 ? "" : command[(colon + 1)..];

        switch (name)
        {
            // ---- keys and orders ----
            case "say": driver.Say(args); break;
            case "idle": driver.WaitUntilReady(60); break;
            case "pace": driver.Pace = double.Parse(args, CultureInfo.InvariantCulture); break;
            case "show": driver.Snapshot(); break;
            case "screen":
                foreach (var line in driver.ScreenText())
                {
                    driver.Output(line.TrimEnd());
                }

                break;
            case "wait": Wait(args); break;
            case "expect":
                if (!driver.Seen(args.Split(';')))
                {
                    throw new ExpectationException($"Expected \"{args}\" on the screen this step.");
                }

                break;
            case "retry": Retry(args); break;
            case "walk": Walk(args); break;
            case "trade": Trade(args); break;
            case "goto": Goto(args); break;
            case "take" or "pack" or "hand" or "wear": Kobayashi(name).Menu(name, args); break;

            // ---- control ----
            case "repeat": Repeat(args); break;
            case "if" or "unless": If(args, name == "if"); break;
            case "mark": _marks.Add(args); break;
            case "ifmark" or "ifnot": IfMarked(args, name == "ifmark"); break;
            case "break": throw new BreakException();

            // ---- looking inside ----
            case "where": Investigation.Where(driver); break;
            case "dump": Investigation.Dump(driver, args); break;
            case "peek": Investigation.Peek(driver, args); break;
            case "poke": Investigation.Poke(driver, args); break;
            case "hold": Investigation.Hold(driver, args); break;
            case "letgo": driver.LetGo(); break;
            case "breakat": Investigation.BreakAt(driver, args); break;
            case "runto": Investigation.RunTo(driver, args); break;
            case "tracebefore": Investigation.TraceBefore(driver, args); break;
            case "log":
                foreach (var line in driver.Log)
                {
                    driver.Output("log: " + line);
                }

                driver.ClearLog();
                break;

            default: Key(command); break;
        }
    }

    // {enter}, {f5}, {down*3}; {w2} or {w1.5} waits so many seconds
    private void Key(string command)
    {
        var parts = command.Split('*');
        int times = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1;
        if (parts[0].StartsWith('w') && double.TryParse(parts[0][1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            driver.RunSeconds(seconds);
            return;
        }

        if (!Keys.Named.TryGetValue(parts[0], out var key))
        {
            throw new FormatException($"There's no key or command '{command}'.");
        }

        for (int t = 0; t < times; t++)
        {
            driver.Press(key.ScanCode, key.Extended);
        }
    }

    // {wait:TEXT;TEXT}: runs, pressing through pauses, until one of the texts shows, 150 seconds at most
    private void Wait(string args)
    {
        var texts = args.Split(';');
        int quarters = 0;
        while (!driver.Shows(texts) && quarters++ < 600 && !driver.Machine.Exited)
        {
            if (driver.Profile.Paused(driver))
            {
                driver.PressNow(Keys.Space);
            }

            driver.Run(driver.InstructionsPerSecond / 4);
        }

        driver.Output(quarters > 600 ? $"[*** no {args}]" : $"[waited {quarters / 4.0:F1}s for {args}]");
    }

    // {retry:TEXT;TEXT>ORDER} or {retry:TEXT>ORDER>TIMES}: gives the order again, eight times at most, until a text shows
    private void Retry(string args)
    {
        var parts = args.Split('>');
        var texts = parts[0].Split(';');
        int most = parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 8, times = 0;
        while (!driver.Shows(texts) && times++ < most && !driver.Machine.Exited)
        {
            driver.Say(parts[1]);
        }

        driver.Output(times > most ? $"[*** no {parts[0]} after {parts[1]}]" : $"[{parts[1]}: {times} time(s)]");
    }

    // {trade:WHO:FIRST>WANT} or {trade:WHO:FIRST>WANT>TIMES}: gives FIRST to WHO, then whatever WHO hands back, until WANT
    // comes back, eight trades at most. What comes back is read from the reply ("He hands you a small disk").
    private void Trade(string args)
    {
        int colon = args.IndexOf(':');
        string who = args[..colon];
        var parts = args[(colon + 1)..].Split('>');
        string item = parts[0], want = parts[1];
        int most = parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 8;
        for (int i = 0; i < most && !driver.Machine.Exited; i++)
        {
            int before = driver.Snapshots.Count;
            var screens = new List<IReadOnlyList<string>> { driver.ScreenText().ToArray() };
            driver.Say($"Give {item} to {who}");
            screens.AddRange(driver.Snapshots.Skip(before));

            // the reply: what the story window gained since the order (the last trade's reply may still
            // be showing), over every screen of it, as it may come in pages, each kept as it was pressed through
            string reply = string.Join(' ', screens.Zip(screens.Skip(1), (a, b) => NewLines(a, b)).SelectMany(lines => lines));
            var got = Given().Matches(reply).LastOrDefault();
            if (got is null)
            {
                driver.Output($"[*** {who} gave nothing back for the {item}]");
                return;
            }

            // "a glass-blend statue of an animal": the thing, without what it's of
            item = got.Groups[1].Value.Split(" of ")[0].Trim();
            driver.Output($"[traded with {who} for {item}]");
            if (item.Contains(want, StringComparison.Ordinal))
            {
                return;
            }
        }

        driver.Output($"[*** no {want} from {who}]");
    }

    // the lines of the story window (the screen but its first three rows, the status line and the place's
    // name, and its last few, the command window) added from one
    // screen to the next: after the longest run of the old window's last lines that starts the new one
    private static IEnumerable<string> NewLines(IReadOnlyList<string> earlier, IReadOnlyList<string> later)
    {
        static string[] Window(IReadOnlyList<string> screen) => screen.Skip(3).Take(15).Select(l => l.Replace('·', ' ').Replace("↓", "").Trim()).ToArray();
        var a = Window(earlier);
        var b = Window(later);
        int overlap = 0;
        for (int k = Math.Min(a.Length, b.Length); k > 0; k--)
        {
            if (a[^k..].SequenceEqual(b[..k]))
            {
                overlap = k;
                break;
            }
        }

        return b[overlap..].Where(l => l.Length > 0);
    }

    [GeneratedRegex(@"(?:hands you|gives you|presents you with|pulls out|you receive from the \w+|you receive) (?:a|an|the) ([a-z][A-Za-z \-]*?)\s*[.,!]")]
    private static partial Regex Given();

    // {walk:DIRECTION:TEXT}: steps with an arrow key until the text shows, 120 steps at most
    private void Walk(string args)
    {
        var parts = args.Split(':', 2);
        var (scanCode, extended) = Keys.Named[parts[0]];
        int steps = 0;
        while (!driver.Shows(parts[1]) && steps++ < 120)
        {
            driver.Press(scanCode, extended);
            driver.RunSeconds(0.25);
            driver.WaitUntilReady();
        }

        driver.Output(driver.Shows(parts[1]) ? $"[walked {steps} {parts[0]} to {parts[1]}]" : $"[*** no {parts[1]} after {steps} steps]");
    }

    // {goto:X.Y}: walks a planet's surface toward a place, by the location panel; the first number grows north, the second east
    private void Goto(string args)
    {
        var want = args.Split('.').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        (int X, int Y)? last = null;
        int steps = 0, stuck = 0;
        while (steps++ < 300 && HereNow() is var (x, y))
        {
            int dx = want[0] - x, dy = want[1] - y, step = StepSize();
            if (Math.Abs(dx) * 2 <= step && Math.Abs(dy) * 2 <= step)
            {
                break;
            }

            bool northSouth = Math.Abs(dx) * 2 > step && (Math.Abs(dx) >= Math.Abs(dy) || Math.Abs(dy) * 2 <= step);
            string key = northSouth ? (dx > 0 ? "up" : "down") : (dy > 0 ? "right" : "left");
            driver.Press(Keys.Named[key].ScanCode, true);
            if (Here() == last)
            {
                if (++stuck > 3)
                {
                    driver.Output($"[*** stuck at {x}.{y}]");
                    break;
                }
            }
            else
            {
                stuck = 0;
            }

            last = Here();
        }

        driver.Output($"[at {Here()} after {steps} steps]");
    }

    // where the location panel says you are; a reply box can hide it, so it's closed first if need be
    private (int X, int Y)? HereNow()
    {
        for (int k = 0; k < 3; k++)
        {
            if (Here() is { } here)
            {
                return here;
            }

            if (KobayashiProfile.ReplyOpen(driver.ScreenText()))
            {
                driver.Press(Keys.Escape);
            }

            driver.RunSeconds(0.5);
            driver.WaitUntilReady();
        }

        return Here();
    }

    private (int X, int Y)? Here()
    {
        var screen = driver.ScreenText();
        for (int i = 0; i < screen.Count - 1; i++)
        {
            if (screen[i].Contains("Location", StringComparison.Ordinal))
            {
                var numbers = Coordinates().Matches(screen[i + 1]);
                if (numbers.Count >= 2)
                {
                    return (int.Parse(numbers[1].Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(numbers[1].Groups[2].Value, CultureInfo.InvariantCulture));
                }
            }
        }

        return null;
    }

    private int StepSize()
    {
        var screen = driver.ScreenText();
        for (int i = 0; i < screen.Count - 1; i++)
        {
            if (screen[i].Contains("Step Size", StringComparison.Ordinal))
            {
                var numbers = Number().Matches(screen[i + 1].Split("Time:")[0]);
                if (numbers.Count > 0)
                {
                    return int.Parse(numbers[^1].Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
        }

        return 25;
    }

    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex Coordinates();

    [GeneratedRegex(@"\b(\d+)\b")]
    private static partial Regex Number();

    // {repeat:N:SCRIPT}: runs the script N times at most, or until a {break} in it
    private void Repeat(string args)
    {
        int colon = args.IndexOf(':');
        int times = int.Parse(args[..colon], CultureInfo.InvariantCulture);
        for (int i = 0; i < times && !driver.Machine.Exited; i++)
        {
            try
            {
                Execute(args[(colon + 1)..]);
            }
            catch (BreakException)
            {
                return;
            }
        }
    }

    // {if:TEXT;TEXT:SCRIPT} runs the script if a text shows; {unless:...} if none does
    private void If(string args, bool when)
    {
        int colon = args.IndexOf(':');
        if (driver.Shows(args[..colon].Split(';')) == when)
        {
            Execute(args[(colon + 1)..]);
        }
    }

    // {ifmark:A,B:SCRIPT} runs the script if every mark is set; {ifnot:A:SCRIPT} if none is
    private void IfMarked(string args, bool when)
    {
        int colon = args.IndexOf(':');
        var names = args[..colon].Split(',');
        if (when ? names.All(_marks.Contains) : !names.Any(_marks.Contains))
        {
            Execute(args[(colon + 1)..]);
        }
    }

    private KobayashiMenus Kobayashi(string command) =>
        driver.Profile is KobayashiProfile ? new KobayashiMenus(driver) : throw new FormatException($"{{{command}}} is for The Kobayashi Alternative's menus.");
}
