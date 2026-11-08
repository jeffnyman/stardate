using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stardate.Player;

/// <summary>
/// How a game is played, where it differs from the defaults: each value is optional, and one
/// given on the command line wins over it for that run.
/// </summary>
internal sealed class Profile
{
    /// <summary>The speed, in DOSBox's cycles: instructions a millisecond.</summary>
    public int? Cycles { get; set; }

    public bool? Sound { get; set; }

    public int? Scale { get; set; }

    public bool? Fullscreen { get; set; }

    public bool? SquarePixels { get; set; }

    /// <summary>
    /// Whether the text is drawn in the CGA's 8 by 8 font, as on the PCs the games were written
    /// for, rather than the VGA's 8 by 16 (see <see cref="Core.Machine.CgaFont"/>).
    /// </summary>
    public bool? CgaFont { get; set; }

    /// <summary>Whether nothing is set.</summary>
    [JsonIgnore]
    public bool IsEmpty => Cycles is null && Sound is null && Scale is null && Fullscreen is null && SquarePixels is null && CgaFont is null;

    /// <summary>The values set in another profile, over this one's.</summary>
    public void Take(Profile other)
    {
        Cycles = other.Cycles ?? Cycles;
        Sound = other.Sound ?? Sound;
        Scale = other.Scale ?? Scale;
        Fullscreen = other.Fullscreen ?? Fullscreen;
        SquarePixels = other.SquarePixels ?? SquarePixels;
        CgaFont = other.CgaFont ?? CgaFont;
    }

    /// <summary>What's set, in a line: "cycles 2000, scale 3, cga font".</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Cycles is { } cycles)
        {
            parts.Add($"cycles {cycles}");
        }

        void Flag(bool? value, string name)
        {
            if (value is { } on)
            {
                parts.Add(on ? name : $"no {name}");
            }
        }

        Flag(Sound, "sound");
        if (Scale is { } scale)
        {
            parts.Add($"scale {scale}");
        }

        Flag(Fullscreen, "full screen");
        Flag(SquarePixels, "square pixels");
        Flag(CgaFont, "cga font");
        return parts.Count == 0 ? "the defaults" : string.Join(", ", parts);
    }
}

/// <summary>
/// The player's settings, in one file in the user's application data (never in a game's folder,
/// which is only read): each game's profile, by its short name. It's plain JSON, to be read and
/// edited by hand as well as by the player.
/// </summary>
internal sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Where the settings are unless told otherwise.</summary>
    public static string DefaultPath => System.IO.Path.Combine(Games.Home, "stardate.json");

    /// <summary>Each game's profile, by its short name.</summary>
    public Dictionary<string, Profile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file they're kept in.</summary>
    [JsonIgnore]
    public string Path { get; private set; } = DefaultPath;

    /// <summary>The settings in a file; empty ones when there's no file yet.</summary>
    /// <exception cref="InvalidDataException">The file isn't settings the player can read.</exception>
    public static Settings Load(string path)
    {
        Settings settings;
        if (File.Exists(path))
        {
            try
            {
                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"the settings in {path} can't be read: {ex.Message}");
            }

            // names are matched without regard to case, as on the command line
            settings.Profiles = new(settings.Profiles ?? [], StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            settings = new Settings();
        }

        settings.Path = path;
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Json) + Environment.NewLine);
    }

    /// <summary>A game's profile; an empty one when none is kept.</summary>
    public Profile ProfileOf(Game game) => Profiles.TryGetValue(game.Name, out var profile) ? profile : new Profile();
}
