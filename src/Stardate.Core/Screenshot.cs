using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Stardate.Core;

/// <summary>Saves what the machine's display shows as a PNG image.</summary>
public static class Screenshot
{
    /// <summary>Writes the display, at its own size, to a stream.</summary>
    public static void WritePng(Machine machine, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(stream);
        int width = machine.DisplayWidth, height = machine.DisplayHeight;
        var pixels = new uint[width * height];
        machine.Render(pixels);

        // each row is a filter byte (0, none) and then the pixels as RGB
        var raw = new byte[height * (1 + (width * 3))];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < width; x++)
            {
                uint rgb = pixels[(y * width) + x];
                raw[o++] = (byte)(rgb >> 16);
                raw[o++] = (byte)(rgb >> 8);
                raw[o++] = (byte)rgb;
            }
        }

        stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(stream, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            Chunk(stream, "IDAT", compressed.ToArray());
        }

        Chunk(stream, "IEND", []);
    }

    /// <summary>Writes the display to a file, making its folder if need be.</summary>
    public static void WritePng(Machine machine, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        WritePng(machine, file);
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);

        var typed = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed, 4);
        stream.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(typed));
        stream.Write(number);
    }

    private static uint Crc(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}
