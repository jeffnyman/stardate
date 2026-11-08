namespace Stardate.Core.Dos;

/// <summary>
/// Drive C: as a program sees it: the game's folder, never written, with a save folder laid over
/// it. A file opened for writing is first copied into the save folder, and from then on the copy
/// is the file. Names are matched without regard to case, as DOS matches them, so the game's
/// files can be in any case on the host.
/// </summary>
/// <remarks>
/// Other folders can be mounted on directories of the drive, read-only as the game's folder is:
/// W.C.A.T. expects the game's data in a directory beside its launcher, and the player puts the
/// game's own folder there rather than copying it. A mounted folder comes before the game
/// folder's directory of the same name, and the save folder before both.
/// </remarks>
internal sealed class DosDrive(string gameFolder, string saveFolder)
{
    // the folders the drive was made with, which a saved state doesn't carry: a state goes
    // into a machine made with folders of its own
    [NotSaved]
    private readonly string _gameFolder = gameFolder;

    [NotSaved]
    private readonly string _saveFolder = saveFolder;

    // folders mounted on directories, by the directory's components
    [NotSaved]
    private readonly List<(string[] At, string Folder)> _mounts = [];

    // files a program deleted that exist in the game's folder, which isn't changed
    private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The current directory, as DOS names it after "C:\", uppercase; empty at the root.</summary>
    public string CurrentDirectory { get; private set; } = "";

    /// <summary>
    /// A DOS path as its components from the root, uppercase, with "." and ".." resolved. Null
    /// when it names another drive, or climbs above the root.
    /// </summary>
    public string[]? Parse(string path)
    {
        path = path.Trim().Replace('/', '\\').ToUpperInvariant();
        if (path.Length >= 2 && path[1] == ':')
        {
            if (path[0] != 'C')
            {
                return null;
            }

            path = path[2..];
        }

        return Resolve(path, CurrentDirectory);
    }

    /// <summary>
    /// A path on a drive, without its letter, as components from the root, uppercase, with "."
    /// and ".." resolved against the drive's current directory. Null when it climbs above the
    /// root. For any drive: the CD's paths are resolved the same way.
    /// </summary>
    public static string[]? Resolve(string path, string currentDirectory)
    {
        path = path.Trim().Replace('/', '\\').ToUpperInvariant();
        var parts = new List<string>();
        if (!path.StartsWith('\\') && currentDirectory.Length > 0)
        {
            parts.AddRange(currentDirectory.Split('\\'));
        }

        foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }

        return [.. parts];
    }

    public static string Join(string[] parts) => string.Join('\\', parts);


    /// <summary>Mounts a host folder on a directory of the drive, read-only.</summary>
    public void Mount(string[] at, string folder) => _mounts.Add((at, folder));

    /// <summary>
    /// The host paths a DOS path could be in, besides the save folder: in each folder mounted
    /// on a directory it's under, then in the game's folder. Each may or may not exist.
    /// </summary>
    private IEnumerable<string> ReadOnly(string[] parts)
    {
        foreach (var (at, folder) in _mounts)
        {
            if (parts.Length >= at.Length && parts.AsSpan(0, at.Length).SequenceEqual(at, StringComparer.OrdinalIgnoreCase)
                && Find(folder, parts[at.Length..]) is { } mounted)
            {
                yield return mounted;
            }
        }

        if (Find(_gameFolder, parts) is { } original)
        {
            yield return original;
        }
    }

    private string? ReadOnlyFile(string[] parts) => ReadOnly(parts).FirstOrDefault(File.Exists);

    /// <summary>The host file a DOS path names, the save folder's copy first; null if there's none.</summary>
    public string? FindFile(string[] parts)
    {
        if (parts.Length == 0)
        {
            return null;
        }

        if (Find(_saveFolder, parts) is { } saved && File.Exists(saved))
        {
            return saved;
        }

        if (_deleted.Contains(Join(parts)))
        {
            return null;
        }

        return ReadOnlyFile(parts);
    }

    public bool DirectoryExists(string[] parts) =>
        parts.Length == 0 ||
        Find(_saveFolder, parts) is { } saved && Directory.Exists(saved) ||
        ReadOnly(parts).Any(Directory.Exists) ||
        _mounts.Any(mount => mount.At.Length >= parts.Length && mount.At.AsSpan(0, parts.Length).SequenceEqual(parts, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The host path to write a DOS path to, in the save folder, creating its directories. With
    /// <paramref name="keep"/>, a file that so far exists only in the game's folder is copied
    /// there first, so writing changes the copy, not the original.
    /// </summary>
    public string ForWriting(string[] parts, bool keep)
    {
        string directory = _saveFolder;
        foreach (var part in parts[..^1])
        {
            directory = Find(directory, [part]) ?? Path.Combine(directory, part);
        }

        Directory.CreateDirectory(directory);
        string target = Find(directory, [parts[^1]]) ?? Path.Combine(directory, parts[^1]);

        if (keep && !File.Exists(target) && FindFile(parts) is { } original)
        {
            File.Copy(original, target);
        }

        _deleted.Remove(Join(parts));
        return target;
    }

    public bool Delete(string[] parts)
    {
        if (FindFile(parts) is not { } file)
        {
            return false;
        }

        if (file.StartsWith(_saveFolder, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
        }

        if (ReadOnlyFile(parts) is not null)
        {
            _deleted.Add(Join(parts));
        }

        return true;
    }

    /// <summary>
    /// Makes a directory, in the save folder: false when something of that name is there
    /// already, or its parent isn't.
    /// </summary>
    public bool MakeDirectory(string[] parts)
    {
        if (parts.Length == 0 || DirectoryExists(parts) || FindFile(parts) is not null || !DirectoryExists(parts[..^1]))
        {
            return false;
        }

        _deleted.Remove(Join(parts));
        Directory.CreateDirectory(Path.Combine(_saveFolder, Path.Combine(parts)));
        return true;
    }

    /// <summary>
    /// Removes an empty directory a program made: false for one that isn't there, isn't
    /// empty, or is the game's own, which isn't changed.
    /// </summary>
    public bool RemoveDirectory(string[] parts)
    {
        if (parts.Length == 0
            || Find(_saveFolder, parts) is not { } saved || !Directory.Exists(saved)
            || ReadOnly(parts).Any(Directory.Exists)
            || List(parts).Any())
        {
            return false;
        }

        Directory.Delete(saved);
        return true;
    }

    public bool ChangeDirectory(string[] parts)
    {
        if (!DirectoryExists(parts))
        {
            return false;
        }

        CurrentDirectory = Join(parts);
        return true;
    }

    /// <summary>The entries of a directory, from every folder, the save folder's taking precedence.</summary>
    public IEnumerable<FileSystemInfo> List(string[] directory)
    {
        var seen = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
        var saved = Find(_saveFolder, directory);
        foreach (var (host, original) in new[] { (saved, false) }.Concat(ReadOnly(directory).Select(path => ((string?)path, true))))
        {
            if (host is null || !Directory.Exists(host))
            {
                continue;
            }

            foreach (var entry in new DirectoryInfo(host).EnumerateFileSystemInfos())
            {
                string name = entry.Name.ToUpperInvariant();
                if (original && _deleted.Contains(Join([.. directory, name])))
                {
                    continue;
                }

                seen.TryAdd(name, entry);
            }
        }

        return seen.Values;
    }

    /// <summary>The host path for DOS components under a root, matching each without regard to case.</summary>
    private static string? Find(string root, string[] parts)
    {
        string current = root;
        foreach (var part in parts)
        {
            if (!Directory.Exists(current))
            {
                return null;
            }

            var match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), part, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return null;
            }

            current = match;
        }

        return current;
    }
}
