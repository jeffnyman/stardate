using System.Text;
using Stardate.Core.Processors;

namespace Stardate.Core.Tests;

/// <summary>
/// The SingleStepTests 80386 set (github.com/SingleStepTests/80386), captured from a real 386EX
/// in real mode: for every opcode, and every combination of it with the operand-size and
/// address-size prefixes, hundreds to thousands of instructions run from random states. Each
/// test is checked for its registers and its memory; the bus cycles aren't, as this processor
/// isn't cycle-accurate.
/// </summary>
/// <remarks>
/// Each test's instruction is followed by a <c>HLT</c>, and an instruction that faults goes on to
/// a <c>HLT</c> at its handler, so a test runs until the processor halts. The test data is 576 MB
/// and isn't in the repository: without it, these tests pass without checking.
/// </remarks>
public class SingleStepTests
{
    private const string Folder = "80386/v1_ex_real_mode";

    private static readonly Lazy<HashSet<string>> Revoked = new(() =>
    {
        var folder = TestData.Find("80386");
        var list = folder is null ? null : Path.Combine(folder, "revocation_list.txt");
        return list is not null && File.Exists(list)
            ? File.ReadAllLines(list).Where(line => line.Length == 40 && !line.StartsWith('#')).ToHashSet()
            : [];
    });

    // the flags each instruction leaves undefined, from the suite's table of opcodes: a mask with
    // those flags clear, by opcode and, for a group, the reg field (as in "0FBA.4")
    private static readonly Lazy<Dictionary<string, uint>> UndefinedFlags = new(() =>
    {
        var masks = new Dictionary<string, uint>();
        var folder = TestData.Find("80386");
        var table = folder is null ? null : Path.Combine(folder, "80386.csv");
        if (table is null || !File.Exists(table))
        {
            return masks;
        }

        var lines = File.ReadAllLines(table);
        var header = SplitCsv(lines[0]);
        int op = Array.IndexOf(header, "op"), ex = Array.IndexOf(header, "ex"), mask = Array.IndexOf(header, "f_umask");
        foreach (var line in lines.Skip(1))
        {
            var fields = SplitCsv(line);
            if (fields[mask].StartsWith("0x", StringComparison.Ordinal))
            {
                string key = fields[ex].Length > 0 ? $"{fields[op]}.{fields[ex]}" : fields[op];
                masks[key] = 0xFFFF_0000 | Convert.ToUInt32(fields[mask], 16);
            }
        }

        return masks;
    });

    /// <summary>
    /// The cases where this processor knowingly differs from the chip, by file and test index.
    /// Each is a behavior no program has reason to depend on, and each is explained in
    /// docs/the-x86-cpu.md.
    /// </summary>
    private static readonly Dictionary<string, (string Why, int[] Cases)> KnownDifferences = new()
    {
        // the 386 fails to detect some negative quotients too big for AL, and returns 80h
        ["F6.7"] = ("IDIV overflow the 386 doesn't detect", [469, 516, 850, 1265, 1754, 2348]),
        ["67F6.7"] = ("IDIV overflow the 386 doesn't detect", [375, 460, 516]),

        // AAM 0 is a divide error, and the 386 leaves flags from its microcode's partial work
        ["D4"] = ("AAM 0's flags", [56, 722, 787, 1049, 1220, 1492, 1699, 1813, 1905, 2024]),

        // a string instruction overwrites the HLT after it, which the chip had already prefetched
        ["67A5"] = ("the prefetch queue", [442]),
        ["6766A5"] = ("the prefetch queue", [442]),
        ["67AB"] = ("the prefetch queue", [458]),
        ["6766AB"] = ("the prefetch queue", [458]),
    };

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        if (TestData.Find(Folder) is { } folder)
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.MOO.gz").Order())
            {
                data.Add(Path.GetFileName(file)[..^".MOO.gz".Length]);
            }
        }
        else
        {
            data.Add("none");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_case_matches_the_chip(string opcode)
    {
        if (TestData.Find(Folder) is not { } folder)
        {
            return;
        }

        var file = MooFile.Load(Path.Combine(folder, opcode + ".MOO.gz"));

        // the table lists each opcode once, without the size prefixes the file names carry
        string bare = opcode;
        while (bare.Length > 2 && (bare.StartsWith("66", StringComparison.Ordinal) || bare.StartsWith("67", StringComparison.Ordinal)))
        {
            bare = bare[2..];
        }

        if (UndefinedFlags.Value.TryGetValue(bare, out uint undefined))
        {
            file.Masks[MooFile.Eflags] = (file.Masks[MooFile.Eflags] ?? 0xFFFF_FFFF) & undefined;
        }

        var bus = new TestBus();
        var failures = new StringBuilder();
        int failed = 0;

        foreach (var test in file.Tests)
        {
            if (Revoked.Value.Contains(test.Hash) ||
                (KnownDifferences.TryGetValue(opcode, out var known) && known.Cases.Contains(test.Index)))
            {
                continue;
            }

            string? difference;
            try
            {
                difference = Run(test, file, bus);
            }
            catch (NotSupportedException e)
            {
                difference = e.Message;
            }

            if (difference is not null && ++failed <= 10)
            {
                failures.AppendLine($"#{test.Index} {test.Name} [{Convert.ToHexString(test.Bytes)}]: {difference}");
            }
        }

        Assert.True(failed == 0, $"{failed} of {file.Tests.Count} cases of {opcode} differ, the first:\n{failures}");
    }

    /// <summary>
    /// A tool for chasing one failing case: set STARDATE_CASE to a file and test index, as in
    /// <c>0FAB:19</c>, and this test runs that case, printing the processor after each step.
    /// </summary>
    [Fact]
    public void Trace_one_case()
    {
        if (Environment.GetEnvironmentVariable("STARDATE_CASE") is not { } chosen || TestData.Find(Folder) is not { } folder)
        {
            return;
        }

        var parts = chosen.Split(':');
        var file = MooFile.Load(Path.Combine(folder, parts[0] + ".MOO.gz"));
        var test = file.Tests.Single(t => t.Index == int.Parse(parts[1]));
        var log = new StringBuilder();
        log.AppendLine($"{test.Name} [{Convert.ToHexString(test.Bytes)}], exception {test.Exception?.Number.ToString() ?? "none"}");
        for (int i = 0; i < MooFile.RegisterNames.Length; i++)
        {
            log.AppendLine($"  {MooFile.RegisterNames[i],-6} {test.Initial[i]:X8} -> {(test.Final[i] is { } f ? f.ToString("X8") : "")}");
        }

        var result = Run(test, file, new TestBus(), cpu => log.AppendLine(
            $"  step: {cpu.Cs:X4}:{cpu.Eip:X8} eax={cpu.Eax:X8} ebx={cpu.Ebx:X8} ecx={cpu.Ecx:X8} edx={cpu.Edx:X8} esp={cpu.Esp:X8} flags={cpu.Eflags:X8} halted={cpu.Halted} fault={cpu.LastFault}"));
        Assert.Fail($"{log}{result ?? "matches"}");
    }

    private static string? Run(MooTest test, MooFile file, TestBus bus, Action<Intel386>? trace = null)
    {
        bus.Load(test.InitialRam);
        var cpu = new Intel386(bus);
        uint Initial(string name) => test.Initial[Array.IndexOf(MooFile.RegisterNames, name)]!.Value;

        cpu.Cr0 = Initial("cr0");
        cpu.Cr3 = Initial("cr3");
        cpu.Eax = Initial("eax");
        cpu.Ebx = Initial("ebx");
        cpu.Ecx = Initial("ecx");
        cpu.Edx = Initial("edx");
        cpu.Esi = Initial("esi");
        cpu.Edi = Initial("edi");
        cpu.Ebp = Initial("ebp");
        cpu.Esp = Initial("esp");
        cpu.Cs = (ushort)Initial("cs");
        cpu.Ds = (ushort)Initial("ds");
        cpu.Es = (ushort)Initial("es");
        cpu.Fs = (ushort)Initial("fs");
        cpu.Gs = (ushort)Initial("gs");
        cpu.Ss = (ushort)Initial("ss");
        cpu.Eip = Initial("eip");
        cpu.Eflags = Initial("eflags");
        cpu.Dr6 = Initial("dr6");
        cpu.Dr7 = Initial("dr7");

        // the instruction, then a HLT, or a fault and a HLT at its handler
        for (int i = 0; i < 4 && !cpu.Halted; i++)
        {
            cpu.Step();
            trace?.Invoke(cpu);
        }

        if (!cpu.Halted)
        {
            return "never reached the HLT";
        }

        uint[] actual =
        [
            cpu.Cr0, cpu.Cr3, cpu.Eax, cpu.Ebx, cpu.Ecx, cpu.Edx, cpu.Esi, cpu.Edi, cpu.Ebp, cpu.Esp,
            cpu.Cs, cpu.Ds, cpu.Es, cpu.Fs, cpu.Gs, cpu.Ss, cpu.Eip, cpu.Eflags, cpu.Dr6, cpu.Dr7,
        ];

        uint flagsMask = Mask(test, file, MooFile.Eflags);
        for (int i = 0; i < actual.Length; i++)
        {
            uint expected = (test.Final[i] ?? test.Initial[i])!.Value;
            uint mask = Mask(test, file, i);
            if ((actual[i] & mask) != (expected & mask))
            {
                return $"{MooFile.RegisterNames[i]} is {actual[i]:X8}, not {expected:X8}" +
                    (mask != 0xFFFF_FFFF ? $" (mask {mask:X8})" : "") +
                    (test.Exception is { } e ? $", exception {e.Number}" : "");
            }
        }

        var memory = new Dictionary<uint, byte>();
        foreach (var (address, value) in test.InitialRam)
        {
            memory[address] = value;
        }

        foreach (var (address, value) in test.FinalRam)
        {
            memory[address] = value;
        }

        foreach (var (address, expected) in memory)
        {
            byte mask = 0xFF;
            if (test.Exception is { } exception && address - exception.FlagsAddress < 2)
            {
                // the flags an exception pushed may include ones the instruction left undefined
                mask = (byte)(flagsMask >> (int)(8 * (address - exception.FlagsAddress)));
            }

            if ((bus.Memory[address] & mask) != (expected & mask))
            {
                return $"[{address:X6}] is {bus.Memory[address]:X2}, not {expected:X2}" +
                    (test.Exception is { } e ? $", exception {e.Number}" : "");
            }
        }

        return null;
    }

    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return [.. fields];
    }

    private static uint Mask(MooTest test, MooFile file, int register)
    {
        uint mask = test.Masks[register] ?? file.Masks[register] ?? 0xFFFF_FFFF;
        string name = MooFile.RegisterNames[register];
        return name.Length == 2 && name[1] == 's' ? mask & 0xFFFF : mask;
    }

    /// <summary>
    /// Memory as the tests see it: 1 MB and the 64 KB above it that real mode can reach. Only
    /// the bytes a test sets are cleared after it. Ports read FFh, as on an empty bus, but for
    /// one inside the chip.
    /// </summary>
    private sealed class TestBus : ICpuBus
    {
        private readonly List<uint> _touched = [];

        public byte[] Memory { get; } = new byte[0x11_0000];

        public void Load(List<(uint Address, byte Value)> ram)
        {
            foreach (var address in _touched)
            {
                Memory[address] = 0;
            }

            _touched.Clear();
            foreach (var (address, value) in ram)
            {
                Memory[address] = value;
                _touched.Add(address);
            }
        }

        public byte Read(uint address) => Memory[address];

        public void Write(uint address, byte value)
        {
            Memory[address] = value;
            _touched.Add(address);
        }

        // ports 22h and 23h are a register inside the 386EX the tests were captured on
        public byte In(ushort port) => port switch
        {
            0x22 => 0x7F,
            0x23 => 0x42,
            _ => 0xFF,
        };

        public void Out(ushort port, byte value)
        {
        }
    }
}
