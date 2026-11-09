namespace Stardate.Player;

/// <summary>A game the player knows, by its short name, and the program that starts it.</summary>
/// <param name="Name">Its short name, on the command line and in the settings: <c>stfc</c>, <c>stka</c> or <c>stpp</c>.</param>
/// <param name="Title">Its title.</param>
/// <param name="Program">The program that starts it, in its folder.</param>
/// <param name="Cycles">Its speed in DOSBox's cycles: instructions a millisecond.</param>
internal sealed record Game(string Name, string Title, string Program, int Cycles)
{
    /// <summary>Its folder, beside the player: <c>games/stka</c> and so on, whether it's there or not.</summary>
    public string Folder => Path.Combine(Games.Root, Name);
}

/// <summary>
/// The games the player knows: the three Star Trek text adventures Simon &amp; Schuster published,
/// which the repository includes in <c>games</c>, and which the build copies beside the player.
/// They're always played from there.
/// </summary>
/// <remarks>
/// <para>
/// <i>First Contact</i> and <i>The Kobayashi Alternative</i> start from their own <c>ST.EXE</c>.
/// <i>The Promethean Prophecy</i> came on a self-booting disk; its folder holds the disk as an
/// image, and <c>RUN.COM</c>, a launcher written much later that starts the game from it. The
/// game's own <c>ST.COM</c> isn't a DOS program, and is never run by itself.
/// </para>
/// <para>
/// All three run at 240 cycles, the speed of the IBM PC's 8088 at 4.77 MHz they were written
/// for. A DOSBox "cycle" is an instruction, and its count is per millisecond, so 240 cycles is
/// 240,000 instructions a second. They time their pauses by loops that count, not by the clock:
/// <i>The Kobayashi Alternative</i>'s credits each stay up for so many passes of an empty loop
/// of 20,000, about a third of a second each on the 8088. Their DOSBox setups ran them at 1,000
/// cycles, at which those pauses go by four times too fast to read.
/// </para>
/// </remarks>
internal static class Games
{
    /// <summary>The speed all three play at, in DOSBox's cycles.</summary>
    public const int Cycles = 240;

    public static readonly Game FirstContact = new("stfc", "Star Trek: First Contact", "ST.EXE", Cycles);
    public static readonly Game KobayashiAlternative = new("stka", "Star Trek: The Kobayashi Alternative", "ST.EXE", Cycles);
    public static readonly Game PrometheanProphecy = new("stpp", "Star Trek: The Promethean Prophecy", "RUN.COM", Cycles);

    /// <summary>Every game the player knows, in the order the menu shows them: as they were published.</summary>
    public static readonly Game[] Known = [FirstContact, KobayashiAlternative, PrometheanProphecy];

    /// <summary>The games' folder, as the build puts it, beside the player.</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "games");

    /// <summary>The game with a short name, or null.</summary>
    public static Game? Named(string name)
        => Known.FirstOrDefault(game => string.Equals(game.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The short names, for messages: "stfc, stka or stpp".</summary>
    public static string Names => $"{string.Join(", ", Known[..^1].Select(game => game.Name))} or {Known[^1].Name}";

    /// <summary>Whether a game is beside the player: its folder, with its program in it.</summary>
    public static bool IsThere(Game game) => FileIn(game.Folder, game.Program) is not null;

    /// <summary>The game's program as it's named in its folder, which the machine is given to load.</summary>
    /// <exception cref="FileNotFoundException">It isn't there.</exception>
    public static string ProgramOf(Game game)
        => FileIn(game.Folder, game.Program) is { } file
            ? Path.GetFileName(file)
            : throw new FileNotFoundException($"no {game.Program} in {game.Folder}: {game.Title} should be there, copied beside the player by the build");

    /// <summary>
    /// The player's own folder in the user's application data: the settings, the saves and the
    /// saved states go there.
    /// </summary>
    public static string Home
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "Stardate");

    /// <summary>
    /// Where a game's saves go unless told otherwise: a folder of its own under the player's
    /// home, named by its short name. It's the game's drive C: as far as writing goes, and the
    /// game's own folder is never written.
    /// </summary>
    public static string SaveFolder(Game game) => Path.Combine(Home, "saves", game.Name);

    /// <summary>
    /// Where a game's saved state goes unless told otherwise: a file for each game, in a folder
    /// beside the saves. Not in the save folder itself, which is the game's drive C:, where the
    /// game would see it.
    /// </summary>
    public static string StateFile(Game game) => Path.Combine(Home, "states", game.Name + ".state");

    // a file in a folder, whatever its case
    private static string? FileIn(string folder, string name)
        => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).FirstOrDefault(file => string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
            : null;
}
