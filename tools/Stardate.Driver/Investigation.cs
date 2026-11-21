using System.Globalization;

namespace Stardate.Driver;

/// <summary>
/// Commands for looking inside a game as it plays: where the processor is, the game's memory,
/// the instructions it ran up to some event, and a breakpoint. Addresses and values are in hex.
/// Memory given by offset alone is in the game's data segment (<see cref="GameProfile.DataSegment"/>).
/// </summary>
internal static class Investigation
{
    /// <summary><c>{where}</c>: where the processor spends its time, sampled 200 times, a thousand instructions apart.</summary>
    public static void Where(Driver driver)
    {
        var counts = new Dictionary<string, int>();
        for (int i = 0; i < 200; i++)
        {
            driver.Run(997);
            string at = $"{driver.Machine.Cpu.Cs:X4}:{driver.Machine.Cpu.Eip:X4}";
            counts[at] = counts.GetValueOrDefault(at) + 1;
        }

        driver.Output("[where " + string.Join(" ", counts.OrderByDescending(c => c.Value).Take(6).Select(c => $"{c.Key}={c.Value}")) + "]");
    }

    /// <summary><c>{dump:SEG:FILE}</c>: writes the 64 KB of a segment to a file, for a disassembler or a comparison.</summary>
    public static void Dump(Driver driver, string args)
    {
        var parts = args.Split(':', 2);
        ushort segment = Hex16(parts[0]);
        var bytes = new byte[0x10000];
        for (uint i = 0; i < bytes.Length; i++)
        {
            bytes[i] = driver.Machine.Read(((uint)segment << 4) + i);
        }

        File.WriteAllBytes(parts[1], bytes);
        driver.Output($"[dumped {segment:X4}:0000 to {parts[1]}]");
    }

    /// <summary><c>{peek:OFF}</c> or <c>{peek:SEG:OFF}</c>: prints a word of memory.</summary>
    public static void Peek(Driver driver, string args)
    {
        var (segment, offset, _) = Address(driver, args, withValue: false);
        driver.Output($"[{segment:X4}:{offset:X4} = {driver.ReadWord(segment, offset):X4} ({driver.ReadWord(segment, offset)})]");
    }

    /// <summary><c>{poke:OFF:VALUE}</c> or <c>{poke:SEG:OFF:VALUE}</c>: writes a word of memory, once.</summary>
    public static void Poke(Driver driver, string args)
    {
        var (segment, offset, value) = Address(driver, args, withValue: true);
        driver.WriteWord(segment, offset, value);
    }

    /// <summary><c>{hold:OFF:VALUE}</c>: holds a word of memory at a value as the game plays, until <c>{letgo}</c>, to keep a countdown from running, say.</summary>
    public static void Hold(Driver driver, string args)
    {
        var (segment, offset, value) = Address(driver, args, withValue: true);
        driver.HoldWord(segment, offset, value);
    }

    /// <summary>
    /// <c>{breakat:SEG:OFF}</c> or <c>{breakat:SEG:OFF:FILE}</c>: runs an instruction at a time
    /// until the processor reaches the address, then prints the registers and the top of the
    /// stack (where the return address and the arguments are) and, with a file, dumps SEG. The
    /// code is dumped then, not before: the games load code in overlays, and what's at an address
    /// changes.
    /// </summary>
    public static void BreakAt(Driver driver, string args)
    {
        var parts = args.Split(':');
        ushort segment = Hex16(parts[0]);
        uint offset = Hex32(parts[1]);
        var cpu = driver.Machine.Cpu;
        long n = 0;
        for (; !(cpu.Cs == segment && cpu.Eip == offset) && !driver.Machine.Exited && n < 400_000_000; n++)
        {
            Step(driver, n);
        }

        uint stack = ((uint)cpu.Ss << 4) + (cpu.Esp & 0xFFFF);
        var words = Enumerable.Range(0, 12).Select(k => (driver.Machine.Read(stack + (uint)k * 2) | (driver.Machine.Read(stack + (uint)k * 2 + 1) << 8)).ToString("X4", CultureInfo.InvariantCulture));
        driver.Output($"[break at {cpu.Cs:X4}:{cpu.Eip:X4} after {n} instructions: {Registers(driver)}; stack {string.Join(" ", words)}]");
        if (parts.Length > 2)
        {
            Dump(driver, $"{parts[0]}:{string.Join(':', parts[2..])}");
        }
    }

    /// <summary><c>{runto:TEXT}</c>: runs an instruction at a time until the machine's log next has the text (a file opened, a seek, a call).</summary>
    public static void RunTo(Driver driver, string text)
    {
        int seen = driver.Log.Count(l => l.Contains(text, StringComparison.Ordinal));
        for (long n = 0; driver.Log.Count(l => l.Contains(text, StringComparison.Ordinal)) == seen && !driver.Machine.Exited && n < 50_000_000; n++)
        {
            Step(driver, n);
        }

        driver.Output($"[at {driver.Machine.Cpu.Cs:X4}:{driver.Machine.Cpu.Eip:X4}, the log having {text}]");
    }

    /// <summary>
    /// <c>{tracebefore:TEXT}</c> or <c>{tracebefore:TEXT:N}</c>: runs an instruction at a time
    /// until the log next has the text, keeping the last N instructions (4,000 by default), and
    /// prints them, each with its registers.
    /// </summary>
    /// <summary>
    /// <c>{traceinto:SEG:FROM:TO}</c> or <c>{traceinto:SEG:FROM:TO:N}</c>: runs an instruction at a time until the
    /// processor reaches SEG:FROM up to SEG:TO, then prints the last N instructions (4000 by default) before it,
    /// each with the first bytes at its address: how a program came to run where it shouldn't.
    /// </summary>
    public static void TraceInto(Driver driver, string args)
    {
        var parts = args.Split(':');
        ushort segment = Hex16(parts[0]), from = Hex16(parts[1]), to = Hex16(parts[2]);
        int keep = parts.Length > 3 ? int.Parse(parts[3], CultureInfo.InvariantCulture) : 4000;
        var ring = new Queue<string>();
        for (long n = 0; !driver.Machine.Exited && n < 400_000_000; n++)
        {
            var cpu = driver.Machine.Cpu;
            if (cpu.Cs == segment && cpu.Eip >= from && cpu.Eip <= to)
            {
                break;
            }

            ring.Enqueue(Instruction(driver));
            if (ring.Count > keep)
            {
                ring.Dequeue();
            }

            Step(driver, n);
        }

        foreach (var line in ring)
        {
            driver.Output("T " + line);
        }

        driver.Output($"[reached {driver.Machine.Cpu.Cs:X4}:{driver.Machine.Cpu.Eip:X4}]");
    }

    public static void TraceBefore(Driver driver, string args)
    {
        var parts = args.Split(':');
        string text = parts[0];
        int keep = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 4000;
        var ring = new Queue<string>();
        int seen = driver.Log.Count(l => l.Contains(text, StringComparison.Ordinal));
        for (long n = 0; driver.Log.Count(l => l.Contains(text, StringComparison.Ordinal)) == seen && !driver.Machine.Exited && n < 400_000_000; n++)
        {
            ring.Enqueue(Instruction(driver));
            if (ring.Count > keep)
            {
                ring.Dequeue();
            }

            Step(driver, n);
        }

        foreach (var line in ring)
        {
            driver.Output("T " + line);
        }
    }

    // an instruction; now and then, a pause is pressed through, as the game would wait at one forever
    private static void Step(Driver driver, long n)
    {
        driver.Run(1);
        if (n % 2_000_000 == 1_999_999 && driver.Profile.Paused(driver))
        {
            driver.PressNow(Keys.Space);
        }
    }

    // an instruction about to run, as a trace shows it: its address, its first bytes, and the registers
    private static string Instruction(Driver driver)
    {
        var cpu = driver.Machine.Cpu;
        uint at = ((uint)cpu.Cs << 4) + (uint)cpu.Eip;
        string bytes = string.Join(' ', Enumerable.Range(0, 6).Select(i => driver.Machine.Read(at + (uint)i).ToString("X2", CultureInfo.InvariantCulture)));
        return $"{cpu.Cs:X4}:{cpu.Eip:X4}  {bytes,-17}  {Registers(driver)}";
    }

    private static string Registers(Driver driver)
    {
        var c = driver.Machine.Cpu;
        return $"ax={c.Eax & 0xFFFF:X4} bx={c.Ebx & 0xFFFF:X4} cx={c.Ecx & 0xFFFF:X4} dx={c.Edx & 0xFFFF:X4} si={c.Esi & 0xFFFF:X4} di={c.Edi & 0xFFFF:X4} ds={c.Ds:X4} es={c.Es:X4} ss={c.Ss:X4} sp={c.Esp & 0xFFFF:X4} bp={c.Ebp & 0xFFFF:X4}";
    }

    // OFF[:VALUE], in the game's data segment, or SEG:OFF[:VALUE]
    private static (ushort Segment, uint Offset, ushort Value) Address(Driver driver, string args, bool withValue)
    {
        var parts = args.Split(':');
        bool segmentGiven = parts.Length == (withValue ? 3 : 2);
        ushort segment = segmentGiven ? Hex16(parts[0])
            : driver.Profile.DataSegment ?? throw new FormatException($"{driver.Profile.Name}'s data segment isn't known: give one, as SEG:OFF.");
        var rest = segmentGiven ? parts[1..] : parts;
        return (segment, Hex32(rest[0]), withValue ? Hex16(rest[1]) : (ushort)0);
    }

    private static ushort Hex16(string s) => ushort.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static uint Hex32(string s) => uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
