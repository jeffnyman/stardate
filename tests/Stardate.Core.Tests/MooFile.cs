using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Stardate.Core.Tests;

/// <summary>
/// A file of SingleStepTests in the MOO format (github.com/dbalsom/moo): a header, then one chunk
/// per test holding the instruction's bytes, the processor's state before and after, and the bus
/// cycles between. Every chunk is a four-letter type, a length and a payload, and chunks this
/// reader doesn't know are skipped by their length. Only what a test of an instruction-stepped
/// processor needs is kept; the bus cycles aren't.
/// </summary>
internal sealed class MooFile
{
    /// <summary>The 386's registers in a test, in the order the format's RG32 chunk lists them.</summary>
    public static readonly string[] RegisterNames =
        ["cr0", "cr3", "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp",
         "cs", "ds", "es", "fs", "gs", "ss", "eip", "eflags", "dr6", "dr7"];

    public const int Eflags = 17;

    public List<MooTest> Tests { get; } = [];

    /// <summary>Masks for the register bits the instruction leaves undefined, for every test in the file.</summary>
    public uint?[] Masks { get; } = new uint?[RegisterNames.Length];

    public static MooFile Load(string path)
    {
        byte[] data;
        using (var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            data = buffer.ToArray();
        }

        var file = new MooFile();
        foreach (var (type, payload) in Chunks(data))
        {
            switch (type)
            {
                case "RM32":
                    ReadRegisters(payload, file.Masks);
                    break;
                case "TEST":
                    file.Tests.Add(ReadTest(payload));
                    break;
            }
        }

        return file;
    }

    private static MooTest ReadTest(ReadOnlySpan<byte> payload)
    {
        var test = new MooTest { Index = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload) };
        foreach (var (type, chunk) in Chunks(payload[4..]))
        {
            switch (type)
            {
                case "NAME":
                    test.Name = Encoding.ASCII.GetString(chunk.AsSpan(4, (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk)));
                    break;
                case "BYTS":
                    test.Bytes = chunk.AsSpan(4, (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk)).ToArray();
                    break;
                case "INIT":
                    ReadState(chunk, test.Initial, test.InitialRam, null);
                    break;
                case "FINA":
                    ReadState(chunk, test.Final, test.FinalRam, test.Masks);
                    break;
                case "EXCP":
                    test.Exception = (chunk[0], BinaryPrimitives.ReadUInt32LittleEndian(chunk[1..]));
                    break;
                case "HASH":
                    test.Hash = Convert.ToHexStringLower(chunk[..20]);
                    break;
            }
        }

        return test;
    }

    private static void ReadState(ReadOnlySpan<byte> payload, uint?[] registers, List<(uint Address, byte Value)> ram, uint?[]? masks)
    {
        foreach (var (type, chunk) in Chunks(payload))
        {
            switch (type)
            {
                case "RG32":
                    ReadRegisters(chunk, registers);
                    break;
                case "RM32" when masks is not null:
                    ReadRegisters(chunk, masks);
                    break;
                case "RAM ":
                    int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk);
                    for (int i = 0; i < count; i++)
                    {
                        var entry = chunk.AsSpan(4 + (5 * i), 5);
                        ram.Add((BinaryPrimitives.ReadUInt32LittleEndian(entry), entry[4]));
                    }

                    break;
            }
        }
    }

    /// <summary>A bitmask of the registers present, then a value for each, in <see cref="RegisterNames"/> order.</summary>
    private static void ReadRegisters(ReadOnlySpan<byte> chunk, uint?[] registers)
    {
        uint present = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        int at = 4;
        for (int i = 0; i < registers.Length; i++)
        {
            if ((present & (1u << i)) != 0)
            {
                registers[i] = BinaryPrimitives.ReadUInt32LittleEndian(chunk[at..]);
                at += 4;
            }
        }
    }

    private static List<(string Type, byte[] Payload)> Chunks(ReadOnlySpan<byte> data)
    {
        var chunks = new List<(string, byte[])>();
        int at = 0;
        while (at + 8 <= data.Length)
        {
            string type = Encoding.ASCII.GetString(data.Slice(at, 4));
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            chunks.Add((type, data.Slice(at + 8, length).ToArray()));
            at += 8 + length;
        }

        return chunks;
    }
}

internal sealed class MooTest
{
    public int Index { get; set; }

    public string Name { get; set; } = "";

    public byte[] Bytes { get; set; } = [];

    public uint?[] Initial { get; } = new uint?[MooFile.RegisterNames.Length];

    /// <summary>The registers the instruction changed; the rest are as they were.</summary>
    public uint?[] Final { get; } = new uint?[MooFile.RegisterNames.Length];

    /// <summary>Masks for register bits this one test leaves undefined.</summary>
    public uint?[] Masks { get; } = new uint?[MooFile.RegisterNames.Length];

    public List<(uint Address, byte Value)> InitialRam { get; } = [];

    public List<(uint Address, byte Value)> FinalRam { get; } = [];

    /// <summary>The exception the instruction raised, and where its flags went on the stack.</summary>
    public (int Number, uint FlagsAddress)? Exception { get; set; }

    public string Hash { get; set; } = "";
}
