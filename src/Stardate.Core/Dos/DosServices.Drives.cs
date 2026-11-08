namespace Stardate.Core.Dos;

// Drives: the games know only C:, the game's folder.
internal sealed partial class DosServices
{
    /// <summary>Drive numbers as DOS counts them in most calls: A: is 0, so C: is 2.</summary>
    private const int DriveC = 2;

    /// <summary>
    /// The highest drive DOS answers for, as function 0Eh reports it: E:, DOS's own default for
    /// LASTDRIVE.
    /// </summary>
    private const int LastDrive = 5;

    /// <summary>
    /// Where a DOS path is on C:, as components from its root, from the current directory when it
    /// isn't given from the root. Null for another drive, or a path that climbs above the root.
    /// </summary>
    private string[]? Locate(string name)
    {
        name = name.Trim();
        bool lettered = name.Length >= 2 && name[1] == ':';
        if (lettered && char.ToUpperInvariant(name[0]) != 'C')
        {
            return null;
        }

        return drive.Parse(lettered ? name : "C:" + name);
    }

    /// <summary>Whether a drive, numbered from 0 for A:, is one of the machine's: only C: is.</summary>
    private static bool DriveExists(int number) => number == DriveC;

    /// <summary>A drive as the calls that number from 1 for A: give it, with 0 for the current drive, numbered from 0.</summary>
    private static int DriveIn(byte number) => number == 0 ? DriveC : number - 1;
}
