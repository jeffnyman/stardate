using System.Text.RegularExpressions;

namespace Stardate.Driver;

/// <summary>
/// What the driver needs to know about one game: where it is and how it starts, how to tell it's
/// waiting for a key, what its "press a key" pauses look like, how an order is given, and how to
/// pick out what it said from the screen.
/// </summary>
internal abstract partial class GameProfile
{
    /// <summary>The games the driver knows, by their short names.</summary>
    public static GameProfile? Named(string name) => name.ToLowerInvariant() switch
    {
        "stfc" => new FirstContactProfile(),
        "stka" => new KobayashiProfile(),
        "stpp" => new PrometheanProfile(),
        _ => null,
    };

    public const string Names = "stfc, stka or stpp";

    /// <summary>The game's short name, and its folder in <c>games</c>.</summary>
    public abstract string Name { get; }

    /// <summary>The program that starts it.</summary>
    public abstract string Program { get; }

    /// <summary>Whether the game wraps its text, so a phrase can run from one line onto the next.</summary>
    public virtual bool TextWraps => false;

    /// <summary>The segment of the game's data, for the investigation commands that read and write it by offset; null if it isn't known.</summary>
    public virtual ushort? DataSegment => null;

    /// <summary>How long the screen must stay still, in seconds, for the game to be ready.</summary>
    public virtual double StillFor => 0.3;

    /// <summary>Whether the game is waiting for a key (the screen is known to be still).</summary>
    public abstract bool WaitingForKey(Driver driver);

    /// <summary>Whether the game has taken the last key typed, so the next can follow; this may run the machine a little.</summary>
    public abstract bool TookKey(Driver driver);

    /// <summary>Whether a "press a key" pause is showing.</summary>
    public abstract bool Paused(Driver driver);

    /// <summary>Gives an order, and waits for the answer.</summary>
    public abstract void Say(Driver driver, string order);

    /// <summary>What the game said, in a set of screens: one line for each thing, in the order seen, without repeats.</summary>
    public abstract IEnumerable<string> Said(IEnumerable<IReadOnlyList<string>> screens);

    protected static string Clean(string text) => Spaces().Replace(text.Replace('·', ' '), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// <i>The Kobayashi Alternative</i>: windows of text over a starfield, orders typed into Kirk's
/// window, the crew's replies in boxes that stay until a key is pressed.
/// </summary>
/// <remarks>
/// The game polls the keyboard itself, in a loop at 108C:0560 to 05A0. It polls while it draws,
/// too, so being in the loop isn't enough: the screen must also be still. Esc closes a reply box,
/// but with none open it moves the cursor along the order line; the line is cleared by Backspace,
/// never past its start, and an order that fills the line ends the game.
/// </remarks>
internal partial class KobayashiProfile : GameProfile
{
    public override string Name => "stka";

    public override string Program => "ST.EXE";

    public override ushort? DataSegment => 0x1A20;

    /// <summary>Where the game's keyboard loops are: each one's code segment, and the range of offsets in it.</summary>
    protected virtual (ushort Cs, ushort From, ushort To)[] KeyboardLoops => [(0x108C, 0x0560, 0x05A0)];

    public override bool WaitingForKey(Driver driver) => InKeyboardLoop(driver, 24) >= 12;

    public override bool TookKey(Driver driver) => InKeyboardLoop(driver, 12) >= 6;

    // how many of so many samples, 487 instructions apart, find the processor in one of the game's keyboard loops
    private int InKeyboardLoop(Driver driver, int samples)
    {
        int hits = 0;
        for (int i = 0; i < samples; i++)
        {
            driver.Run(487);
            var cpu = driver.Machine.Cpu;
            if (KeyboardLoops.Any(loop => cpu.Cs == loop.Cs && cpu.Eip >= loop.From && cpu.Eip < loop.To))
            {
                hits++;
            }
        }

        return hits;
    }

    // "Hit any key to continue" in The Kobayashi Alternative, "Press any key to continue" in First Contact
    public override bool Paused(Driver driver) => driver.Shows("Hit any key to continue", "Press any key to continue");

    /// <summary>The key that closes a reply box; null if the next order does.</summary>
    protected virtual byte? CloseReply => Keys.Escape;

    public override void Say(Driver driver, string order)
    {
        if (CloseReply is { } close && ReplyOpen(driver.ScreenText()))
        {
            driver.Press(close);
            driver.WaitUntilReady();
        }

        for (int n = 0; n < 70 && Typed(driver) > 0; n++)
        {
            driver.PressNow(Keys.Backspace);
            driver.WaitForEcho();
        }

        driver.Type(order);
        driver.EnterAndWait();
    }

    /// <summary>Whether a reply is showing: a box titled with a speaker, not the order window's or a panel's.</summary>
    public static bool ReplyOpen(IReadOnlyList<string> screen)
    {
        for (int i = 1; i < screen.Count; i++)
        {
            foreach (Match box in BoxTitle().Matches(screen[i]))
            {
                if (!NotAReply(box.Groups[1].Value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool NotAReply(string title) =>
        title.StartsWith("Kirk", StringComparison.Ordinal) || title.StartsWith("U.S.S", StringComparison.Ordinal)
        || title.StartsWith("Planet Surface", StringComparison.Ordinal) || title.StartsWith("PAUSE", StringComparison.Ordinal);

    // how many characters are on the order line, the Kirk window's second line, before the cursor
    private static int Typed(Driver driver)
    {
        var screen = driver.ScreenText();
        int position = driver.Machine.Read(0x450) | (driver.Machine.Read(0x451) << 8);
        int column = position & 0xFF, row = position >> 8;
        for (int i = 0; i < screen.Count - 2; i++)
        {
            if (screen[i].Contains("┌ Kirk ", StringComparison.Ordinal))
            {
                int input = i + 2, prompt = screen[input].IndexOf('►');
                return row == input && prompt >= 0 && column > prompt + 1 ? column - prompt - 1 : 0;
            }
        }

        return 0;
    }

    [GeneratedRegex(@"┌ ([^─┐]+?) ─")]
    private static partial Regex BoxTitle();

    /// <summary>The replies, as "Speaker: words", and the places, as "[Deck 7 - Transporter]", as they change.</summary>
    public override IEnumerable<string> Said(IEnumerable<IReadOnlyList<string>> screens)
    {
        var seen = new HashSet<string>();
        string? place = null;
        foreach (var screen in screens)
        {
            for (int i = 0; i < screen.Count; i++)
            {
                foreach (Match box in BoxTitle().Matches(screen[i]))
                {
                    string title = box.Groups[1].Value.Trim();
                    if (box.Index <= 4 && i == 0)
                    {
                        // the room's window: its title is the place
                        if (title != place)
                        {
                            place = title;
                            yield return $"[{title}]";
                        }

                        continue;
                    }

                    if (NotAReply(title))
                    {
                        continue;
                    }

                    var words = new List<string>();
                    for (int k = i + 1; k < Math.Min(screen.Count, i + 9); k++)
                    {
                        if (screen[k].Length <= box.Index)
                        {
                            break;
                        }

                        string part = screen[k][box.Index..Math.Min(screen[k].Length, box.Index + box.Length + 30)];
                        if (part.StartsWith('└'))
                        {
                            break;
                        }

                        int end = part.IndexOf('│', 1);
                        words.Add(part[1..(end > 0 ? end : part.Length)]);
                    }

                    string line = $"{title}: {Clean(string.Join(' ', words))}";
                    if (seen.Add(line))
                    {
                        yield return line;
                    }
                }
            }
        }
    }
}

/// <summary>
/// <i>The Promethean Prophecy</i>: a story window, a status line at the top, and a command
/// window at the foot, where sentences are typed after a <c>//</c> prompt.
/// </summary>
/// <remarks>
/// The game reads keys through the BIOS. It's ready for an order when the BIOS's keyboard buffer
/// is empty, the screen is still, and the command window shows a fresh <c>//</c> prompt: the
/// screen alone isn't enough, as the game can clear its story window and then work for a while
/// before it writes. When the story window fills, it stops at a <c>↓</c> for a key to show the
/// rest.
/// </remarks>
internal sealed class PrometheanProfile : GameProfile
{
    public override string Name => "stpp";

    public override string Program => "RUN.COM";

    public override bool TextWraps => true;

    // ready for the next order: the keys all taken, and a fresh prompt in the command window (while the game
    // works, the order stays there after the prompt, and the story window can stay still for a while)
    public override bool WaitingForKey(Driver driver) =>
        driver.KeyboardBufferEmpty() && (driver.ScreenText().Any(line => line.Replace('·', ' ').Trim() == "//") || Paused(driver));

    public override bool TookKey(Driver driver)
    {
        driver.RunSeconds(0.025);
        return driver.KeyboardBufferEmpty();
    }

    public override bool Paused(Driver driver) =>
        driver.ScreenText().Any(line => line.Replace('·', ' ').Trim() == "↓");

    public override void Say(Driver driver, string order)
    {
        driver.Type(order);
        driver.EnterAndWait();
    }

    /// <summary>The story window's new lines, screen by screen.</summary>
    public override IEnumerable<string> Said(IEnumerable<IReadOnlyList<string>> screens)
    {
        var shown = new HashSet<string>();
        foreach (var screen in screens)
        {
            // the story window: below the status line, above the command window
            foreach (var raw in screen.Skip(2).Take(16))
            {
                string line = Clean(raw.Replace("↓", ""));
                if (line.Length > 0 && shown.Add(line))
                {
                    yield return line;
                }
            }
        }
    }
}

/// <summary>
/// <i>First Contact</i>: <i>The Kobayashi Alternative</i>'s engine a year earlier, with the same
/// windows, menus and order line, and pictures drawn in the CGA's graphics mode between them.
/// </summary>
internal sealed class FirstContactProfile : KobayashiProfile
{
    public override string Name => "stfc";

    public override ushort? DataSegment => null;

    // a reply's pause box comes up to a second after it, and the game polls the keyboard in between
    public override double StillFor => 1.2;

    // a reply stays until the next order: Esc moves the cursor, as it does in The Kobayashi
    // Alternative when no reply is open, and other keys are typed
    protected override byte? CloseReply => null;

    // found with {where}: at the order prompt, and at the computer's ::Inquiry:: prompt
    protected override (ushort Cs, ushort From, ushort To)[] KeyboardLoops => [(0x0E78, 0x06E8, 0x0728), (0x23A2, 0x2A40, 0x2A80)];

    // a picture, in a graphics mode, is a pause too: it waits for a key, in the BIOS (found with {where},
    // at F000:0184), and the driver, reading no text from it, would otherwise see a still, blank screen
    public override bool Paused(Driver driver) => base.Paused(driver) || ShowingPicture(driver);

    /// <summary>The replies and places, as in <i>The Kobayashi Alternative</i>, and the story in the room's window, a line at a time.</summary>
    public override IEnumerable<string> Said(IEnumerable<IReadOnlyList<string>> screens)
    {
        var all = screens.ToList();
        var seen = new HashSet<string>();
        foreach (var screen in all)
        {
            // the room's window: from the second row to its foot, its text between the borders at columns 4 and 75,
            // less any line another box covers
            for (int row = 1; row < screen.Count && !screen[row].TrimStart().StartsWith('└'); row++)
            {
                string line = screen[row];
                if (line.Length < 76 || line[4] != '│')
                {
                    continue;
                }

                string text = line[5..75];
                if (text.IndexOfAny(['│', '┌', '└', '─']) < 0 && Clean(text) is { Length: > 0 } words && seen.Add(words))
                {
                    yield return words;
                }
            }
        }

        foreach (var line in base.Said(all))
        {
            yield return line;
        }
    }

    private static bool ShowingPicture(Driver driver)
    {
        if (driver.Machine.VideoMode is not (4 or 5 or 6))
        {
            return false;
        }

        int inBios = 0;
        for (int i = 0; i < 8; i++)
        {
            driver.Run(487);
            if (driver.Machine.Cpu.Cs == 0xF000)
            {
                inBios++;
            }
        }

        return inBios >= 6;
    }
}
