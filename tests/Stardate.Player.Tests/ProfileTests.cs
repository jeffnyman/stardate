using Stardate.Core;

namespace Stardate.Player.Tests;

/// <summary>
/// Profiles and the command line, on the games the build copies beside the player: each run is
/// headless and short, the settings live in a temporary file, and the saves in a temporary folder.
/// </summary>
public sealed class ProfileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("stardate-player").FullName;

    public ProfileTests()
    {
        SettingsPath = Path.Combine(_root, "home", "stardate.json");
        Saves = Path.Combine(_root, "saves");
    }

    private string SettingsPath { get; }

    private string Saves { get; }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private int Run(params string[] args)
        => Program.Run([.. args, "--settings", SettingsPath, "--saves", Saves, "--headless", "--seconds", "1"]);

    [Fact]
    public void No_settings_file_is_empty_settings()
    {
        var settings = Settings.Load(SettingsPath);
        Assert.Empty(settings.Profiles);
        Assert.True(settings.ProfileOf(Games.KobayashiAlternative).IsEmpty);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Settings_are_kept_and_read_back_whatever_the_case_of_their_names()
    {
        var settings = Settings.Load(SettingsPath);
        settings.Profiles["STKA"] = new Profile { Scale = 3, Sound = false, CgaFont = true };
        settings.Save();

        var profile = Settings.Load(SettingsPath).ProfileOf(Games.KobayashiAlternative);
        Assert.Equal(3, profile.Scale);
        Assert.False(profile.Sound);
        Assert.True(profile.CgaFont);
        Assert.Null(profile.Cycles);
        Assert.Equal("no sound, scale 3, cga font", profile.ToString());
        Assert.True(Settings.Load(SettingsPath).ProfileOf(Games.FirstContact).IsEmpty);

        // plain JSON, by the options' names
        Assert.Contains("\"cgaFont\": true", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Settings_that_cannot_be_read_say_so()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{ not settings");
        var ex = Assert.Throws<InvalidDataException>(() => Settings.Load(SettingsPath));
        Assert.Contains(SettingsPath, ex.Message);
    }

    [Fact]
    public void A_profile_takes_only_what_another_sets()
    {
        var profile = new Profile { Cycles = 2000, CgaFont = true, Sound = false };
        profile.Take(new Profile { CgaFont = false, Scale = 2 });
        Assert.Equal(2000, profile.Cycles);
        Assert.False(profile.CgaFont);
        Assert.False(profile.Sound);
        Assert.Equal(2, profile.Scale);
        Assert.Equal("the defaults", new Profile().ToString());
    }

    [Theory]
    [InlineData("stfc", "Star Trek: First Contact", "ST.EXE")]
    [InlineData("STKA", "Star Trek: The Kobayashi Alternative", "ST.EXE")]
    [InlineData("stpp", "Star Trek: The Promethean Prophecy", "RUN.COM")]
    public void Games_are_known_by_their_short_names(string name, string title, string program)
    {
        var game = Games.Named(name)!;
        Assert.Equal(title, game.Title);
        Assert.Equal(program, game.Program);
        Assert.Equal(240, game.Cycles);
        Assert.Null(Games.Named("wc1"));
        Assert.Equal("stfc, stka or stpp", Games.Names);
    }

    [Theory]
    [InlineData("stfc")]
    [InlineData("stka")]
    [InlineData("stpp")]
    public void The_games_are_played_from_beside_the_player(string name)
    {
        // the build copies the repository's games beside the player
        var game = Games.Named(name)!;
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "games", name), game.Folder);
        Assert.True(Games.IsThere(game));
        Assert.Equal(game.Program, Games.ProgramOf(game), ignoreCase: true);
    }

    [Fact]
    public void The_promethean_prophecys_own_program_is_never_the_one_run()
        => Assert.Equal("RUN.COM", Games.PrometheanProphecy.Program);

    [Theory]
    [InlineData("stfc")]
    [InlineData("stka")]
    [InlineData("stpp")]
    public void Each_game_plays_headless_for_five_seconds_and_shows_a_picture(string name)
    {
        string picture = Path.Combine(_root, name + ".png");
        Assert.Equal(0, Program.Run([name, "--settings", SettingsPath, "--saves", Saves, "--headless", "--seconds", "5", "--screenshot", picture]));
        Assert.Equal((640, 400), PngSize(picture));
    }

    [Fact]
    public void The_cga_font_halves_the_picture_and_is_remembered()
    {
        string picture = Path.Combine(_root, "cga.png");
        Assert.Equal(0, Run("stka", "--cga-font", "--remember", "--screenshot", picture));
        Assert.Equal((640, 200), PngSize(picture));
        Assert.True(Settings.Load(SettingsPath).ProfileOf(Games.KobayashiAlternative).CgaFont);

        // remembered, it's the game's own from then on, and only that game's
        Assert.Equal(0, Run("stka", "--screenshot", picture));
        Assert.Equal((640, 200), PngSize(picture));
        Assert.Equal(0, Run("stfc", "--screenshot", picture));
        Assert.Equal((640, 400), PngSize(picture));

        // and given otherwise for a run, it's otherwise for that run
        Assert.Equal(0, Run("stka", "--no-cga-font", "--screenshot", picture));
        Assert.Equal((640, 400), PngSize(picture));
        Assert.True(Settings.Load(SettingsPath).ProfileOf(Games.KobayashiAlternative).CgaFont);
    }

    [Fact]
    public void Options_are_kept_only_when_asked_and_forgotten_when_asked()
    {
        Assert.Equal(0, Run("stka", "--cycles", "2000"));
        Assert.True(Settings.Load(SettingsPath).ProfileOf(Games.KobayashiAlternative).IsEmpty);

        Assert.Equal(0, Run("stka", "--cycles", "2000", "--no-sound", "--remember"));
        var profile = Settings.Load(SettingsPath).ProfileOf(Games.KobayashiAlternative);
        Assert.Equal(2000, profile.Cycles);
        Assert.False(profile.Sound);

        // what's remembered is added to, not replaced
        Assert.Equal(0, Run("stka", "--scale", "3", "--square-pixels", "--remember"));
        profile = Settings.Load(SettingsPath).ProfileOf(Games.KobayashiAlternative);
        Assert.Equal("cycles 2000, no sound, scale 3, square pixels", profile.ToString());
        Assert.ThrowsAny<Exception>(() => Run("stka", "--scale", "9"));
        Assert.ThrowsAny<Exception>(() => Run("stka", "--cycles", "5"));

        Assert.Equal(0, Run("stka", "--forget"));
        Assert.True(Settings.Load(SettingsPath).ProfileOf(Games.KobayashiAlternative).IsEmpty);
    }

    [Fact]
    public void Mistakes_on_the_command_line_are_said()
    {
        Assert.Contains("stfc, stka or stpp", Assert.ThrowsAny<Exception>(() => Run("wc1")).Message);
        Assert.Contains("one game at a time", Assert.ThrowsAny<Exception>(() => Run("stka", "stfc")).Message);
        Assert.Contains("unknown option", Assert.ThrowsAny<Exception>(() => Run("stka", "--mouse")).Message);
        Assert.Contains("--seconds", Assert.ThrowsAny<Exception>(() => Program.Run(["stka", "--headless"])).Message);
        Assert.Contains("needs a game", Assert.ThrowsAny<Exception>(() => Program.Run(["--headless", "--seconds", "1"])).Message);
        Assert.Equal(0, Program.Run(["--help"]));
        Assert.Equal(0, Program.Run(["--version"]));
        Assert.Equal(0, Program.Run(["--list", "--settings", SettingsPath]));
    }

    [Fact]
    public void A_saved_state_is_resumed_from_its_file()
    {
        // one for each game, beside the saves rather than in them, where the game would see it
        Assert.Equal(Path.Combine(Games.Home, "states", "stka.state"), Games.StateFile(Games.KobayashiAlternative));
        Assert.Equal(Path.Combine(Games.Home, "saves", "stka"), Games.SaveFolder(Games.KobayashiAlternative));

        string state = Path.Combine(_root, "stka.state");
        var ex = Assert.Throws<FileNotFoundException>(() => Run("stka", "--resume", "--state", state));
        Assert.Contains("no state saved", ex.Message);

        // a state of the game a couple of seconds in
        var machine = new Machine(Games.KobayashiAlternative.Folder, Saves);
        machine.Load("ST.EXE");
        Assert.True(machine.Run(2 * machine.InstructionsPerSecond));
        using (var file = File.Create(state))
        {
            machine.SaveState(file);
        }

        Assert.Equal(0, Run("stka", "--resume", "--state", state));

        File.WriteAllText(state, "not a state");
        Assert.Throws<InvalidDataException>(() => Run("stka", "--resume", "--state", state));
    }

    [Fact]
    public void The_menu_lists_the_games()
    {
        var settings = Settings.Load(SettingsPath);
        settings.Profiles["stka"] = new Profile { CgaFont = true };
        string picture = Path.Combine(AppContext.BaseDirectory, "screenshots", "menu.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
        Sdl.SdlMenu.Snapshot(settings, picture, selected: 1, message: "a message");

        // a 32-bit BMP: its pixels start where its header says
        byte[] bmp = File.ReadAllBytes(picture);
        Assert.Equal((byte)'B', bmp[0]);
        int start = BitConverter.ToInt32(bmp, 10);
        var colors = new HashSet<int>();
        for (int i = start; i + 4 <= bmp.Length; i += 4)
        {
            colors.Add(BitConverter.ToInt32(bmp, i) & 0xFFFFFF);
        }

        // the background, the bar, and text in four colors at least
        Assert.True(colors.Count >= 6, $"only {colors.Count} colors");
    }

    /// <summary>A PNG's width and height, from its header.</summary>
    private static (int Width, int Height) PngSize(string path)
    {
        byte[] png = File.ReadAllBytes(path);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }
}
