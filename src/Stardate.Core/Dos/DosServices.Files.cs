using System.Text;

namespace Stardate.Core.Dos;

// Files: handles, reading, writing, seeking, directories and searches.
internal sealed partial class DosServices
{
    // handles 0 to 4 are the standard devices: input, output, error, auxiliary, printer
    private const int FirstFileHandle = 5;

    private readonly Dictionary<int, OpenFile> _files = new()
    {
        [0] = OpenFile.Device("CON"),
        [1] = OpenFile.Device("CON"),
        [2] = OpenFile.Device("CON"),
        [3] = OpenFile.Device("AUX"),
        [4] = OpenFile.Device("PRN"),
    };

    // searches in progress, by the address of the disk transfer area each one fills
    private readonly Dictionary<uint, Queue<Found>> _searches = [];

    /// <summary>A file or directory a search found.</summary>
    private sealed class Found
    {
        public string Name { get; init; } = "";

        public bool IsDirectory { get; init; }

        public long Size { get; init; }

        public DateTime Time { get; init; }

        public static Found From(FileSystemInfo entry) => new()
        {
            Name = entry.Name.ToUpperInvariant(),
            IsDirectory = entry is DirectoryInfo,
            Size = entry is FileInfo file ? file.Length : 0,
            Time = entry.LastWriteTime,
        };
    }

    /// <summary>
    /// An open file or device. A file opened for writing is read from the game's folder until the
    /// first write, which moves it to its copy in the save folder: games open their data files for
    /// writing as a matter of course, and only copying what they really change keeps the save
    /// folder to their saves.
    /// </summary>
    private sealed class OpenFile
    {
        public string Name { get; init; } = "";

        public FileStream? Stream { get; set; }

        public string[]? Parts { get; init; }

        public bool Writable { get; init; }

        public bool Copied { get; set; }

        public bool IsDevice => Stream is null;

        public long Position
        {
            get => Stream!.Position;
            set => Stream!.Position = value;
        }

        public long Length => Stream!.Length;

        public static OpenFile Device(string name) => new() { Name = name };
    }

    /// <summary>
    /// Closes every file the program has open, without telling it: before a saved state is
    /// loaded over the machine, which opens the state's own files again.
    /// </summary>
    public void CloseFiles()
    {
        foreach (var file in _files.Values)
        {
            file.Stream?.Dispose();
        }
    }

    /// <summary>Closes the files a program left open when it ends, as DOS does; the devices stay.</summary>
    private void CloseProgramFiles()
    {
        foreach (int handle in _files.Keys.Where(handle => handle >= FirstFileHandle).ToList())
        {
            _files[handle].Stream?.Dispose();
            _files.Remove(handle);
        }

        _searches.Clear();
    }

    private void PrepareToWrite(OpenFile file)
    {
        if (file.Copied || file.Parts is null || file.Stream is not { } stream)
        {
            return;
        }

        long position = stream.Position;
        stream.Dispose();
        string target = drive.ForWriting(file.Parts, keep: true);
        file.Stream = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite) { Position = position };
        file.Copied = true;
    }

    /// <summary>The INT 21h functions that deal with files; false for any other.</summary>
    private bool FileCall(int function, Registers cpu)
    {
        switch (function)
        {
            case 0x39: MakeDirectory(cpu); return true;
            case 0x3A: RemoveDirectory(cpu); return true;
            case 0x3B: ChangeDirectory(cpu); return true;
            case 0x3C: Create(cpu, replace: true); return true;
            case 0x3D: Open(cpu); return true;
            case 0x3E: Close(cpu); return true;
            case 0x3F: ReadFile(cpu); return true;
            case 0x40: WriteFile(cpu); return true;
            case 0x41: Delete(cpu); return true;
            case 0x42: Seek(cpu); return true;
            case 0x43: Attributes(cpu); return true;
            case 0x44: Ioctl(cpu); return true;
            case 0x45: Duplicate(cpu); return true;
            case 0x47: CurrentDirectory(cpu); return true;
            case 0x4E: FindFirst(cpu); return true;
            case 0x4F: FindNext(); return true;
            case 0x56: Rename(cpu); return true;
            case 0x57: FileTime(cpu); return true;
            case 0x5B: Create(cpu, replace: false); return true;
            default: return false;
        }
    }

    private string NameAt(ushort segment, ushort offset) => machine.ReadString(segment, offset);

    private int NewHandle(OpenFile file)
    {
        int handle = FirstFileHandle;
        while (_files.ContainsKey(handle))
        {
            handle++;
        }

        _files[handle] = file;
        return handle;
    }

    private static bool IsDeviceName(string name, out string device)
    {
        device = Path.GetFileNameWithoutExtension(name.Replace('\\', '/').Split('/')[^1]).ToUpperInvariant();
        return device is "CON" or "NUL" or "PRN" or "AUX";
    }

    private void Open(Registers cpu)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        int mode = cpu.Al & 7;

        if (IsDeviceName(name, out string device))
        {
            int deviceHandle = NewHandle(OpenFile.Device(device));
            cpu.Ax = (ushort)deviceHandle;
            Ok();
            machine.Trace($"21/3D open device {device}: handle {deviceHandle}");
            return;
        }

        string[]? parts = Locate(name);
        string? file = parts is null ? null : drive.FindFile(parts);
        if (parts is null || file is null)
        {
            machine.Trace($"21/3D open {name}: not found");
            Fail(2);
            return;
        }

        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int handle = NewHandle(new OpenFile { Name = DosDrive.Join(parts), Stream = stream, Parts = parts, Writable = mode != 0 });
        cpu.Ax = (ushort)handle;
        Ok();
        machine.Trace($"21/3D open {name} ({(mode == 0 ? "read" : "write")}): handle {handle}, {stream.Length} bytes");
    }

    private void Create(Registers cpu, bool replace)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        if (Locate(name) is not { Length: > 0 } parts || !drive.DirectoryExists(parts[..^1]))
        {
            Fail(3);
            return;
        }

        if (!replace && drive.FindFile(parts) is not null)
        {
            Fail(0x50);
            return;
        }

        string file = drive.ForWriting(parts, keep: false);
        var stream = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        int handle = NewHandle(new OpenFile { Name = DosDrive.Join(parts), Stream = stream, Parts = parts, Writable = true, Copied = true });
        cpu.Ax = (ushort)handle;
        Ok();
        machine.Trace($"21/{(replace ? "3C" : "5B")} create {name}: handle {handle}");
    }

    private bool TryFile(Registers cpu, out OpenFile file)
    {
        if (_files.TryGetValue(cpu.Bx, out file!))
        {
            return true;
        }

        Fail(6);
        return false;
    }

    private void Close(Registers cpu)
    {
        if (TryFile(cpu, out var file))
        {
            _files.Remove(cpu.Bx);
            if (!_files.ContainsValue(file))
            {
                // the last handle to it, duplicates included
                file.Stream?.Dispose();
            }

            Ok();
            machine.Trace($"21/3E close {cpu.Bx} ({file.Name})");
        }
    }

    private void ReadFile(Registers cpu)
    {
        if (!TryFile(cpu, out var file))
        {
            return;
        }

        int count = 0;
        if (file.Stream is { } stream)
        {
            var buffer = new byte[(int)Math.Clamp(file.Length - file.Position, 0, cpu.Cx)];
            count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            uint address = ((uint)cpu.Ds << 4) + cpu.Dx;
            for (int i = 0; i < count; i++)
            {
                machine.Write(address + (uint)i, buffer[i]);
            }
        }

        machine.Trace($"21/3F read {cpu.Cx} bytes from {cpu.Bx} ({file.Name}) to {cpu.Ds:X4}:{cpu.Dx:X4}: {count}");
        cpu.Ax = (ushort)count;
        Ok();
    }

    private void WriteFile(Registers cpu)
    {
        if (!TryFile(cpu, out var file))
        {
            return;
        }

        var buffer = new byte[cpu.Cx];
        uint address = ((uint)cpu.Ds << 4) + cpu.Dx;
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = machine.Read(address + (uint)i);
        }

        if (file.Stream is not null && !file.Writable)
        {
            Fail(5);
            return;
        }

        PrepareToWrite(file);
        if (file.Stream is { } stream)
        {
            if (buffer.Length == 0)
            {
                // a write of nothing truncates the file at the current position
                stream.SetLength(stream.Position);
            }

            stream.Write(buffer);
            machine.Trace($"21/40 write {buffer.Length} bytes to {cpu.Bx} ({file.Name})");
        }
        else if (file.Name == "CON")
        {
            foreach (byte b in buffer)
            {
                ConsoleOut((char)b);
            }
        }

        cpu.Ax = (ushort)buffer.Length;
        Ok();
    }

    private void Seek(Registers cpu)
    {
        if (!TryFile(cpu, out var file))
        {
            return;
        }

        long position = 0;
        if (file.Stream is { } stream)
        {
            int offset = (cpu.Cx << 16) | cpu.Dx;
            long target = cpu.Al switch
            {
                0 => (uint)offset,
                1 => file.Position + offset,
                _ => file.Length + offset,
            };

            if (target < 0)
            {
                Fail(0x19);
                return;
            }

            position = file.Position = target;
        }

        cpu.Dx = (ushort)(position >> 16);
        cpu.Ax = (ushort)position;
        Ok();
        machine.Trace($"21/42 seek {cpu.Bx} ({file.Name}) to {position}");
    }

    private void Delete(Registers cpu)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        if (Locate(name) is { } parts && drive.Delete(parts))
        {
            Ok();
            machine.Trace($"21/41 delete {name}");
        }
        else
        {
            Fail(2);
        }
    }

    private void MakeDirectory(Registers cpu)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        var parts = Locate(name);
        bool made = parts is not null && drive.MakeDirectory(parts);
        if (made)
        {
            Ok();
        }
        else
        {
            // access denied: DOS's answer when it's there already, as well as when it can't be made
            Fail(parts is { Length: > 0 } p && drive.DirectoryExists(p[..^1]) ? 5 : 3);
        }

        machine.Trace($"21/39 make directory {name}: {(made ? "made" : "failed")}");
    }

    private void RemoveDirectory(Registers cpu)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        bool removed = Locate(name) is { } parts && drive.RemoveDirectory(parts);
        if (removed)
        {
            Ok();
        }
        else
        {
            Fail(5);
        }

        machine.Trace($"21/3A remove directory {name}: {(removed ? "removed" : "failed")}");
    }

    private void Rename(Registers cpu)
    {
        string from = NameAt(cpu.Ds, cpu.Dx), to = NameAt(cpu.Es, cpu.Di);
        if (Locate(from) is not { } source || drive.FindFile(source) is not { } file || Locate(to) is not { } target)
        {
            Fail(2);
            return;
        }

        byte[] contents = File.ReadAllBytes(file);
        drive.Delete(source);
        File.WriteAllBytes(drive.ForWriting(target, keep: false), contents);
        Ok();
        machine.Trace($"21/56 rename {from} to {to}");
    }

    private void Attributes(Registers cpu)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        var parts = Locate(name);
        if (parts is not null && drive.FindFile(parts) is not null)
        {
            if (cpu.Al == 0)
            {
                cpu.Cx = 0x20;
            }

            Ok();
        }
        else if (parts is not null && drive.DirectoryExists(parts))
        {
            cpu.Cx = 0x10;
            Ok();
        }
        else
        {
            Fail(2);
        }
    }

    /// <summary>INT 21h AH=44h, I/O control: mostly telling devices from files.</summary>
    private void Ioctl(Registers cpu)
    {
        switch (cpu.Al)
        {
            case 0x00:
                if (TryFile(cpu, out var file))
                {
                    // device information: bit 7 for a device (with 80h-83h for the console's
                    // roles), and for a file the drive number, C: being 2
                    cpu.Dx = file.IsDevice ? (ushort)(file.Name == "CON" ? 0x80D3 : 0x80C0) : (ushort)DriveC;
                    Ok();
                }

                break;
            case 0x01: Ok(); break;
            case 0x06:
            case 0x07:
                if (TryFile(cpu, out _))
                {
                    cpu.Al = 0xFF;
                    Ok();
                }

                break;
            case 0x08:
                // whether a drive (BL: 0 for the current one, 1 for A:) is removable. C: isn't.
                if (!DriveExists(DriveIn(cpu.Bl)))
                {
                    Fail(0x0F);
                }
                else
                {
                    cpu.Ax = 1;
                    Ok();
                }

                break;
            case 0x09:
                // whether a drive is remote: C: isn't
                if (!DriveExists(DriveIn(cpu.Bl)))
                {
                    Fail(0x0F);
                    break;
                }

                cpu.Dx = 0;
                Ok();
                break;
            case 0x0E:
                cpu.Al = 0; // the drive has only the one letter
                Ok();
                break;
            case 0x0F:
                Ok();
                break;
            default:
                throw new NotSupportedException($"INT 21h function 44h subfunction {cpu.Al:X2}h isn't provided.");
        }
    }

    private void Duplicate(Registers cpu)
    {
        if (!TryFile(cpu, out var file))
        {
            return;
        }

        // the duplicate shares the original's position, as the same open file
        cpu.Ax = (ushort)NewHandle(file);
        Ok();
    }

    private void ChangeDirectory(Registers cpu)
    {
        string name = NameAt(cpu.Ds, cpu.Dx);
        if (Locate(name) is { } parts && drive.ChangeDirectory(parts))
        {
            Ok();
        }
        else
        {
            Fail(3);
        }

        machine.Trace($"21/3B change directory to {name}");
    }

    private void CurrentDirectory(Registers cpu)
    {
        if (!DriveExists(DriveIn(cpu.Dl)))
        {
            Fail(0x0F);
            return;
        }

        machine.WriteBytes(cpu.Ds, cpu.Si, [.. Encoding.ASCII.GetBytes(drive.CurrentDirectory), 0]);
        Ok();
    }

    private void FileTime(Registers cpu)
    {
        if (!TryFile(cpu, out var file))
        {
            return;
        }

        if (cpu.Al == 0)
        {
            var time = file.Stream is { } stream ? File.GetLastWriteTime(stream.Name) : DateTime.Now;
            (cpu.Cx, cpu.Dx) = DosTime(time);
        }

        Ok();
    }

    private static (ushort Time, ushort Date) DosTime(DateTime t) =>
        ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)),
         (ushort)(((Math.Max(t.Year, 1980) - 1980) << 9) | (t.Month << 5) | t.Day));

    // ---- searches ----------------------------------------------------------------------------

    private uint Dta => ((uint)_dtaSegment << 4) + _dtaOffset;

    /// <summary>
    /// INT 21h AH=4Eh: the first file matching a pattern with wildcards, into the disk transfer
    /// area. The rest of the matches wait for AH=4Fh, keyed by that area.
    /// </summary>
    private void FindFirst(Registers cpu)
    {
        string pattern = NameAt(cpu.Ds, cpu.Dx);
        int attributes = cpu.Cx;
        if (Locate(pattern) is not { Length: > 0 } parts || !drive.DirectoryExists(parts[..^1]))
        {
            Fail(3);
            return;
        }

        var matches = new Queue<Found>(drive.List(parts[..^1]).Select(Found.From)
            .Where(entry => Matches(entry.Name, parts[^1]))
            .Where(entry => !entry.IsDirectory || (attributes & 0x10) != 0)
            .OrderBy(entry => entry.Name, StringComparer.Ordinal));
        _searches[Dta] = matches;
        machine.Trace($"21/4E find {pattern}: {matches.Count} matches");

        if (!NextMatch())
        {
            Fail(2);
        }
    }

    private void FindNext()
    {
        if (!NextMatch())
        {
            Fail(0x12);
        }
    }

    private bool NextMatch()
    {
        if (!_searches.TryGetValue(Dta, out var matches) || !matches.TryDequeue(out var entry))
        {
            return false;
        }

        // the search's record: 21 bytes DOS keeps for itself, then the attribute, time, date,
        // size and name of the file found
        var (time, date) = DosTime(entry.Time);
        uint size = (uint)Math.Min(entry.Size, uint.MaxValue);
        machine.WriteBytes(_dtaSegment, _dtaOffset, new byte[43]);
        machine.WriteByte(_dtaSegment, _dtaOffset + 0x15u, (byte)(entry.IsDirectory ? 0x10 : 0x20));
        machine.WriteWord(_dtaSegment, _dtaOffset + 0x16u, time);
        machine.WriteWord(_dtaSegment, _dtaOffset + 0x18u, date);
        machine.WriteWord(_dtaSegment, _dtaOffset + 0x1Au, (ushort)size);
        machine.WriteWord(_dtaSegment, _dtaOffset + 0x1Cu, (ushort)(size >> 16));
        machine.WriteBytes(_dtaSegment, _dtaOffset + 0x1Eu, [.. Encoding.ASCII.GetBytes(entry.Name), 0]);
        Ok();
        return true;
    }

    /// <summary>
    /// Whether a name matches a DOS wildcard pattern, the name part and extension apart: "?"
    /// matches any one character, "*" the rest of its part.
    /// </summary>
    internal static bool Matches(string name, string pattern)
    {
        static (string Name, string Extension) Split(string s)
        {
            int dot = s.LastIndexOf('.');
            return dot < 0 ? (s, "") : (s[..dot], s[(dot + 1)..]);
        }

        static bool Part(string text, string pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == '*')
                {
                    return true;
                }

                if (i >= text.Length)
                {
                    return pattern[i..].All(c => c is '?' or '*');
                }

                if (pattern[i] != '?' && pattern[i] != text[i])
                {
                    return false;
                }
            }

            return text.Length == pattern.Length;
        }

        var (n, e) = Split(name);
        var (pn, pe) = Split(pattern);
        if (pattern == "*.*" || pattern == "*")
        {
            return true;
        }

        return Part(n, pn) && Part(e, pe);
    }
}
