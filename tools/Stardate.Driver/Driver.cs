using System.Text.RegularExpressions;
using Stardate.Core;

namespace Stardate.Driver;

/// <summary>
/// A game on a machine, played from outside it: keys pressed as a player would press them, each
/// one only when the game is ready for it.
/// </summary>
/// <remarks>
/// <para>
/// The hard part of playing a game of the 1980s by program is knowing when it's ready for a key.
/// On a 4.77 MHz 8088 the game takes a while to draw what it has to say, and a key pressed while
/// it does is lost, or taken to close what's being drawn. So before each key the driver waits
/// for the game to be ready: its screen still (but for the starfield's twinkling and the clock),
/// and the game waiting for a key as its <see cref="GameProfile"/> knows how to tell.
/// </para>
/// <para>
/// Time passes in the machine only as the driver runs it, so everything here is in the machine's
/// time, and a run from the same state with the same script plays the same way every time.
/// </para>
/// </remarks>
internal sealed partial class Driver
{
    private readonly List<string> _log = [];
    private readonly Dictionary<uint, ushort> _held = [];

    public Driver(Machine machine, GameProfile profile)
    {
        Machine = machine;
        Profile = profile;
        machine.Log = _log.Add;
    }

    public Machine Machine { get; }

    public GameProfile Profile { get; }

    /// <summary>The machine's log: DOS and BIOS calls, as they happen.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>Whether a "press a key" pause is left waiting, rather than pressed through.</summary>
    public bool StopAtPauses { get; set; }

    /// <summary>Seconds to wait after each order, as a player would, reading: a route sets it with an <c>@pace</c> line.</summary>
    public double Pace { get; set; }

    /// <summary>Text to print as it plays, for the script's <c>{show}</c> and the investigation commands.</summary>
    public Action<string> Output { get; set; } = Console.WriteLine;

    /// <summary>The screens seen after each order and at each <c>{show}</c>, for what the game said.</summary>
    public List<IReadOnlyList<string>> Snapshots { get; } = [];

    public int InstructionsPerSecond => Machine.InstructionsPerSecond;

    /// <summary>Runs the machine for so many seconds of its time.</summary>
    public void RunSeconds(double seconds) => Run((long)(seconds * InstructionsPerSecond));

    public void Run(long instructions)
    {
        Machine.Run(instructions);
        Hold();
    }

    public void ClearLog() => _log.Clear();

    // ---- what's on the screen ---------------------------------------------------------------

    public IReadOnlyList<string> ScreenText() => Machine.ScreenText();

    public bool Shows(params string[] texts) => Showing(ScreenText(), texts);

    /// <summary>Whether a text was on the screen at any time this step: now, after an order, or at a pause.</summary>
    public bool Seen(params string[] texts) => Shows(texts) || Snapshots.Any(screen => Showing(screen, texts));

    /// <summary>Whether a text is on the screen now, or was on one kept since the snapshot numbered <paramref name="since"/>, as at a pause pressed through.</summary>
    public bool SeenSince(int since, params string[] texts) => Shows(texts) || Snapshots.Skip(since).Any(screen => Showing(screen, texts));

    // a text on one line, or, for a game whose text wraps, across the end of one and the start of the next
    private bool Showing(IReadOnlyList<string> screen, string[] texts)
    {
        if (screen.Any(line => texts.Any(t => line.Contains(t, StringComparison.Ordinal))))
        {
            return true;
        }

        // what was said, as the profile puts it back together: a reply wraps inside its box
        if (Profile.Said([screen]).Any(line => texts.Any(t => line.Contains(t, StringComparison.Ordinal))))
        {
            return true;
        }

        if (!Profile.TextWraps)
        {
            return false;
        }

        string joined = WhiteSpace().Replace(string.Join(' ', screen.Select(l => l.Replace('·', ' ').Trim())), " ");
        return texts.Any(t => joined.Contains(t, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    /// <summary>The screen, less what changes when nothing's happening: the twinkling stars and the clock.</summary>
    public string StillScreen() => StillParts().Replace(string.Join('\n', ScreenText()).Replace('·', ' '), "");

    [GeneratedRegex(@"Time:\d\d \d\d \d\d")]
    private static partial Regex StillParts();

    public void Snapshot() => Snapshots.Add(ScreenText().ToArray());

    // ---- when the game is ready --------------------------------------------------------------

    /// <summary>Whether the game is waiting for a key: its screen stays still for 0.3 seconds, and its profile agrees.</summary>
    public bool Ready()
    {
        string before = StillScreen();
        Run((int)(InstructionsPerSecond * Profile.StillFor));
        return StillScreen() == before && Profile.WaitingForKey(this) && !Profile.Paused(this);
    }

    /// <summary>Whether the BIOS's keyboard buffer is empty: the game has taken every key pressed so far.</summary>
    public bool KeyboardBufferEmpty() => Machine.Read(0x41A) == Machine.Read(0x41C);

    /// <summary>
    /// Waits until the game is ready for a key, for at most so many seconds of the machine's time;
    /// a "press a key" pause is pressed through, unless <see cref="StopAtPauses"/>.
    /// </summary>
    public void WaitUntilReady(double seconds = 40)
    {
        // the machine's own time, as each look at the screen runs the game for the profile's StillFor
        var until = Machine.Time + TimeSpan.FromSeconds(seconds);
        while (Machine.Time < until && !Machine.Exited)
        {
            if (Profile.Paused(this))
            {
                if (StopAtPauses)
                {
                    Output("[paused]");
                    return;
                }

                PressThroughPause();
                continue;
            }

            if (Ready())
            {
                return;
            }

            RunSeconds(0.1);
        }

        if (!Machine.Exited)
        {
            Output($"[*** not ready after {seconds} seconds]");
        }
    }

    /// <summary>
    /// Presses Space at a pause, then waits for the screen to change, three seconds at most, so as not to
    /// press again while the pause's box is still drawn: a key the pause didn't take goes to the order line,
    /// and in First Contact the clock stops while a line is being typed. The seconds it took.
    /// </summary>
    public double PressThroughPause()
    {
        // the pause's screen is kept, for what was said: it may be gone for good after the key
        RunSeconds(0.5);
        Snapshot();
        string before = StillScreen();
        PressNow(Keys.Space);
        double waited = 0.6;
        for (int i = 0; i < 30 && StillScreen() == before && !Machine.Exited; i++)
        {
            RunSeconds(0.1);
            waited += 0.1;
        }

        return waited;
    }

    // ---- keys ----------------------------------------------------------------------------------

    /// <summary>Presses a key now, ready or not: held for a fiftieth of a second, then let go.</summary>
    public void PressNow(byte scanCode, bool extended = false)
    {
        Machine.PressKey(scanCode, extended);
        RunSeconds(0.02);
        Machine.ReleaseKey(scanCode, extended);
        RunSeconds(0.05);
    }

    /// <summary>
    /// Presses a key once the game is ready for it. An arrow key's move is taken up a moment
    /// after the key, so after one the driver waits for the screen to change, and then for the
    /// game again.
    /// </summary>
    public void Press(byte scanCode, bool extended = false)
    {
        WaitUntilReady();
        string before = StillScreen();
        PressNow(scanCode, extended);
        if (extended)
        {
            for (int i = 0; i < 30 && StillScreen() == before; i++)
            {
                RunSeconds(0.1);
            }

            WaitUntilReady();
        }
    }

    /// <summary>
    /// Types text: the first key once the game is ready, the rest at a typist's pace, each once
    /// the game has taken the one before. Within a line the game only echoes, so it needn't be
    /// ready in full.
    /// </summary>
    public void Type(string text)
    {
        bool first = true;
        foreach (char ch in text)
        {
            if (first)
            {
                WaitUntilReady();
            }
            else
            {
                WaitForEcho();
            }

            first = false;
            TypeNow(ch);
        }
    }

    /// <summary>Waits, a second at most, for the game to take the last key typed.</summary>
    public void WaitForEcho()
    {
        for (int i = 0; i < 40 && !Profile.TookKey(this); i++)
        {
            RunSeconds(0.02);
        }
    }

    public void TypeNow(char ch)
    {
        var (scanCode, shift) = Keys.ForChar(ch) ?? throw new ArgumentException($"There's no key for '{ch}'.", nameof(ch));
        if (shift)
        {
            Machine.PressKey(Keys.LeftShift);
            RunSeconds(0.01);
            PressNow(scanCode);
            Machine.ReleaseKey(Keys.LeftShift);
        }
        else
        {
            PressNow(scanCode);
        }
    }

    /// <summary>Gives an order: whatever's open is closed, the order typed, Enter pressed, and the game's answer waited for.</summary>
    public void Say(string order)
    {
        Profile.Say(this, order);
        Snapshot();
        if (Pace > 0)
        {
            RunSeconds(Pace);
        }
    }

    /// <summary>After Enter: waits for the screen to change, three seconds at most, then for the game to be ready again.</summary>
    public void EnterAndWait()
    {
        string before = StillScreen();
        WaitForEcho();
        PressNow(Keys.Enter);
        for (int i = 0; i < 30 && StillScreen() == before; i++)
        {
            RunSeconds(0.1);
        }

        WaitUntilReady(60);
    }

    // ---- the game's data ---------------------------------------------------------------------

    /// <summary>A word of the machine's memory, at a segment and offset.</summary>
    public ushort ReadWord(ushort segment, uint offset)
    {
        uint address = ((uint)segment << 4) + offset;
        return (ushort)(Machine.Read(address) | (Machine.Read(address + 1) << 8));
    }

    public void WriteWord(ushort segment, uint offset, ushort value)
    {
        uint address = ((uint)segment << 4) + offset;
        Machine.Write(address, (byte)value);
        Machine.Write(address + 1, (byte)(value >> 8));
    }

    /// <summary>Holds a word of memory at a value: it's written again after every run of the machine, until let go.</summary>
    public void HoldWord(ushort segment, uint offset, ushort value)
    {
        _held[((uint)segment << 4) + offset] = value;
        Hold();
    }

    public void LetGo() => _held.Clear();

    private void Hold()
    {
        foreach (var (address, value) in _held)
        {
            Machine.Write(address, (byte)value);
            Machine.Write(address + 1, (byte)(value >> 8));
        }
    }
}
