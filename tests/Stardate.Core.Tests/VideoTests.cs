namespace Stardate.Core.Tests;

/// <summary>The color graphics adapter and the video BIOS, with small programs.</summary>
public sealed class VideoTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("stardate-game").FullName;
    private readonly string _saves = Directory.CreateTempSubdirectory("stardate-saves").FullName;

    public void Dispose()
    {
        Directory.Delete(_game, recursive: true);
        Directory.Delete(_saves, recursive: true);
    }

    [Fact]
    public void The_character_set_has_all_256()
    {
        Assert.Equal('A', CodePage437.ToChar(0x41));
        Assert.Equal('╔', CodePage437.ToChar(0xC9));
        Assert.Equal('█', CodePage437.ToChar(0xDB));
        Assert.Equal('■', CodePage437.ToChar(0xFE));
        Assert.Equal(' ', CodePage437.ToChar(0xFF));
    }

    [Fact]
    public void Teletype_wraps_and_scrolls_at_the_bottom()
    {
        // mov cx, 26; next: mov ax, 0E41h ('A'); int 10h; mov ax, 0E0Dh; int 10h; mov ax, 0E0Ah;
        // int 10h; loop next; mov ax, 0E42h; int 10h; mov ax, 4C00h; int 21h
        var machine = Run(
        [
            0xB9, 0x1A, 0x00,
            0xB8, 0x41, 0x0E, 0xCD, 0x10, 0xB8, 0x0D, 0x0E, 0xCD, 0x10, 0xB8, 0x0A, 0x0E, 0xCD, 0x10, 0xE2, 0xEF,
            0xB8, 0x42, 0x0E, 0xCD, 0x10,
            0xB8, 0x00, 0x4C, 0xCD, 0x21,
        ]);

        // 26 lines of A, each ending in a line feed, on 25 rows: the screen has scrolled twice,
        // the 26th A has gone up to row 23, and the B is on the last row
        var text = machine.ScreenText();
        Assert.All(text.Take(24), line => Assert.Equal("A", line));
        Assert.Equal("B", text[24]);
    }

    [Fact]
    public void Characters_are_written_in_their_colors_and_the_window_scrolls()
    {
        //   mov ah, 2; xor bh, bh; mov dx, 0105h; int 10h           cursor to row 1, column 5
        //   mov ax, 0958h ('X'); mov bx, 001Eh (yellow on blue); mov cx, 3; int 10h
        //   mov ax, 0601h; mov bh, 70h; xor cx, cx; mov dx, 184Fh; int 10h   scroll all up by one
        //   mov ax, 4C00h; int 21h
        var machine = Run(
        [
            0xB4, 0x02, 0x30, 0xFF, 0xBA, 0x05, 0x01, 0xCD, 0x10,
            0xB8, 0x58, 0x09, 0xBB, 0x1E, 0x00, 0xB9, 0x03, 0x00, 0xCD, 0x10,
            0xB8, 0x01, 0x06, 0xB7, 0x70, 0x31, 0xC9, 0xBA, 0x4F, 0x18, 0xCD, 0x10,
            0xB8, 0x00, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal("     XXX", machine.ScreenText()[0]);

        // the X at row 0, column 5 is drawn yellow on blue; the new bottom row is in black on gray
        var pixels = new uint[machine.DisplayWidth * machine.DisplayHeight];
        machine.Render(pixels);
        var cell = Enumerable.Range(0, 16).SelectMany(y => Enumerable.Range(40, 8).Select(x => pixels[(y * 640) + x])).ToHashSet();
        Assert.Equal([0xFF0000AA, 0xFFFFFF55], cell.Order());
        Assert.Equal(0xFFAAAAAA, pixels[(399 * 640) + 300]);
    }

    [Fact]
    public void A_cga_graphics_mode_draws_its_pixels_in_the_palette()
    {
        //   mov ax, 0004h; int 10h                     320 by 200 in four colors
        //   mov ax, 0C03h; mov cx, 10; mov dx, 1; int 10h   a pixel of color 3 at (10, 1)
        //   mov ax, 4C00h; int 21h
        var machine = Run(
        [
            0xB8, 0x04, 0x00, 0xCD, 0x10,
            0xB8, 0x03, 0x0C, 0xB9, 0x0A, 0x00, 0xBA, 0x01, 0x00, 0xCD, 0x10,
            0xB8, 0x00, 0x4C, 0xCD, 0x21,
        ]);

        Assert.Equal(4, machine.VideoMode);
        var pixels = new uint[machine.DisplayWidth * machine.DisplayHeight];
        machine.Render(pixels);

        // color 3 of the BIOS's palette 1 is white; line 1 is the picture's lines 2 and 3, and
        // pixel 10 its pixels 20 and 21
        Assert.Equal(0xFFFFFFFF, pixels[(2 * 640) + 20]);
        Assert.Equal(0xFFFFFFFF, pixels[(3 * 640) + 21]);
        Assert.Equal(0xFF000000, pixels[(2 * 640) + 22]);
    }

    [Fact]
    public void The_cga_font_makes_the_picture_200_lines()
    {
        var machine = new Machine(_game, _saves) { CgaFont = true };
        Assert.Equal((640, 200), (machine.DisplayWidth, machine.DisplayHeight));
    }

    private Machine Run(byte[] code)
    {
        File.WriteAllBytes(Path.Combine(_game, "TEST.EXE"), DosTests.Exe(code));
        var log = new List<string>();
        var machine = new Machine(_game, _saves) { Log = log.Add };
        machine.Load("TEST.EXE");
        machine.Run(100_000);
        Assert.True(machine.Exited, string.Join('\n', log));
        return machine;
    }
}
