namespace Stardate.Core.Devices;

/// <summary>
/// The display adapter, as the games see it: IBM's Color Graphics Adapter, the card they were
/// written for, with its 16-color text and its graphics modes, at B800:0000. The text is drawn
/// with the VGA's 8 by 16 font by default, as on the later PCs these games were mostly played
/// on; with <see cref="CgaFont"/> it's the CGA's own 8 by 8 font, as on the machines they
/// were first sold for.
/// </summary>
/// <remarks>
/// <para>
/// The adapter's memory is the machine's, not its own: the processor writes it as plain memory,
/// at full speed, and the adapter reads it when it draws. The BIOS (in <see cref="Machine"/>)
/// keeps what a program asks for in the data area at 0040h, as a real one does, and sets the
/// adapter's registers to match, so a program that goes to the registers directly, to move the
/// cursor say, is shown as faithfully as one that calls the BIOS.
/// </para>
/// <para>
/// The picture is always 640 pixels wide, and 400 lines high with the VGA's font or 200 with
/// the CGA's: a 40-column mode, or a 320-pixel graphics mode, has its pixels drawn twice as
/// wide, and with the VGA's font a graphics mode's lines are drawn twice, as the VGA scanned
/// them, so that the size never changes under the player.
/// </para>
/// </remarks>
internal sealed class VideoAdapter(byte[] memory)
{
    /// <summary>Where the adapter's memory starts, as a linear address: B800:0000.</summary>
    public const uint MemoryStart = 0xB_8000;

    /// <summary>The adapter's memory: 32 KB, as the VGA has in its color text modes (the CGA had 16).</summary>
    public const int MemorySize = 0x8000;

    // the screen refreshes 60 times a second; text blinks every 16 frames, the cursor every 8
    private const double FrameRate = 60;
    private const int TextBlinkFrames = 16;
    private const int CursorBlinkFrames = 8;

    // the CGA's sixteen colors, with its dark yellow made brown as IBM's monitor did
    private static readonly uint[] Palette =
    [
        0xFF000000, 0xFF0000AA, 0xFF00AA00, 0xFF00AAAA, 0xFFAA0000, 0xFFAA00AA, 0xFFAA5500, 0xFFAAAAAA,
        0xFF555555, 0xFF5555FF, 0xFF55FF55, 0xFF55FFFF, 0xFFFF5555, 0xFFFF55FF, 0xFFFFFF55, 0xFFFFFFFF,
    ];

    private static readonly byte[] Font8 = LoadFont("Font8x8.bin");
    private static readonly byte[] Font16 = LoadFont("Font8x16.bin");

    // the CRT controller's registers, of which the cursor's shape and place and the display's
    // start matter here
    private readonly byte[] _crtc = new byte[18];
    private int _crtcIndex;

    /// <summary>
    /// The video mode, as the BIOS set it: 0 to 3 are text, 4 to 6 graphics. Modes 0, 2 and 5 are
    /// the "black and white" ones, which only turn the color signal off for a composite monitor
    /// or television; on IBM's color display, which took the card's colors directly, they're in
    /// color like the rest, and so they're drawn here.
    /// </summary>
    public int Mode { get; private set; } = 3;

    /// <summary>Port 3D8h, the mode control register: bit 5 makes attribute bit 7 blink the text rather than brighten its background.</summary>
    public byte ModeControl { get; set; } = 0x29;

    /// <summary>Port 3D9h, the color select register: the border, or a graphics mode's background, and its palette.</summary>
    public byte ColorSelect { get; set; }

    /// <summary>Whether the text is drawn with the CGA's 8 by 8 font, rather than the VGA's 8 by 16.</summary>
    public bool CgaFont { get; set; }

    /// <summary>The time, in seconds, for the blinking and the retrace bits.</summary>
    public double Time { get; set; }

    /// <summary>Whether the mode is a text mode.</summary>
    public bool IsText => Mode <= 3;

    /// <summary>The columns of text across the screen: 40 or 80.</summary>
    public int Columns => Mode is 0 or 1 or 4 or 5 ? 40 : 80;

    /// <summary>The rows of text down it.</summary>
    public const int Rows = 25;

    /// <summary>The picture's width in pixels.</summary>
    public const int Width = 640;

    /// <summary>The picture's height in pixels: 200 lines, scanned twice with the VGA's font.</summary>
    public int Height => CgaFont ? 200 : 400;

    /// <summary>
    /// Sets a mode as the BIOS does, with the registers to match: the display starting at the
    /// memory's start, and an underline cursor.
    /// </summary>
    public void SetMode(int mode)
    {
        Mode = mode;
        ModeControl = mode switch
        {
            0 => 0x2C,
            1 => 0x28,
            2 => 0x2D,
            3 => 0x29,
            4 => 0x2A,
            5 => 0x2E,
            _ => 0x1E,
        };
        ColorSelect = (byte)(mode == 6 ? 0x3F : 0x30);
        _crtc[0x0C] = _crtc[0x0D] = 0;
        _crtc[0x0A] = 6;
        _crtc[0x0B] = 7;
    }

    /// <summary>The cursor's place, as an offset in characters from the memory's start: CRT controller registers 0Eh and 0Fh.</summary>
    public int CursorAddress
    {
        get => (_crtc[0x0E] << 8) | _crtc[0x0F];
        set
        {
            _crtc[0x0E] = (byte)(value >> 8);
            _crtc[0x0F] = (byte)value;
        }
    }

    /// <summary>The cursor's shape: its first and last scan lines of the CGA's eight, and bits 5 and 6 of the first to hide it.</summary>
    public (byte Start, byte End) CursorShape
    {
        get => (_crtc[0x0A], _crtc[0x0B]);
        set => (_crtc[0x0A], _crtc[0x0B]) = value;
    }

    /// <summary>Where the display starts in memory, in characters (words) for text and in bytes over two for graphics.</summary>
    public int StartAddress
    {
        get => (_crtc[0x0C] << 8) | _crtc[0x0D];
        set
        {
            _crtc[0x0C] = (byte)(value >> 8);
            _crtc[0x0D] = (byte)value;
        }
    }

    public byte In(int port) => port switch
    {
        0x3D4 => (byte)_crtcIndex,
        0x3D5 => _crtcIndex is >= 0x0C and < 0x12 ? _crtc[_crtcIndex] : (byte)0,
        0x3DA => Status(),
        _ => 0xFF,
    };

    public void Out(int port, byte value)
    {
        switch (port)
        {
            case 0x3D4: _crtcIndex = value & 0x1F; break;
            case 0x3D5:
                if (_crtcIndex < _crtc.Length)
                {
                    _crtc[_crtcIndex] = value;
                }

                break;
            case 0x3D8: ModeControl = value; break;
            case 0x3D9: ColorSelect = value; break;
        }
    }

    /// <summary>
    /// Port 3DAh, the status: bit 0 while the beam isn't drawing (between lines, and through the
    /// vertical retrace), and bit 3 through the vertical retrace. Programs for the CGA wait for
    /// these before writing its memory, which on the real card made snow on the screen.
    /// </summary>
    private byte Status()
    {
        const int totalLines = 262, visibleLines = 200;
        double frame = Time * FrameRate;
        double line = (frame - Math.Floor(frame)) * totalLines;
        bool vertical = line >= 224 && line < 228;
        bool blank = line >= visibleLines || (line - Math.Floor(line)) > 0.8;
        return (byte)((vertical ? 0x08 : 0) | (blank ? 0x01 : 0));
    }

    // ---- drawing -----------------------------------------------------------------------------

    /// <summary>Draws the picture, <see cref="Width"/> by <see cref="Height"/> pixels of 32-bit ARGB, row by row.</summary>
    public void Render(Span<uint> pixels)
    {
        var picture = pixels[..(Width * Height)];
        if ((ModeControl & 0x08) == 0)
        {
            // the video is turned off
            picture.Fill(Palette[0]);
        }
        else if (IsText)
        {
            RenderText(picture);
        }
        else
        {
            RenderGraphics(picture);
        }
    }

    private void RenderText(Span<uint> pixels)
    {
        long frame = (long)(Time * FrameRate);
        bool textOn = (frame / TextBlinkFrames) % 2 == 0;
        bool cursorOn = (frame / CursorBlinkFrames) % 2 == 0;
        bool blink = (ModeControl & 0x20) != 0;

        int columns = Columns;
        int cellWidth = Width / columns;
        int cellHeight = CgaFont ? 8 : 16;
        byte[] font = CgaFont ? Font8 : Font16;
        int start = StartAddress;
        int cursor = CursorAddress;
        var (cursorStart, cursorEnd) = CursorShape;
        bool cursorShown = cursorOn && (cursorStart & 0x60) != 0x20;

        // the cursor's lines are given in the CGA's eight; with the VGA's sixteen, they're doubled
        int scale = cellHeight / 8;
        int firstLine = (cursorStart & 0x1F) * scale, lastLine = ((cursorEnd & 0x1F) * scale) + scale - 1;

        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int cell = start + (row * columns) + column;
                int address = (cell * 2) & (MemorySize - 1);
                byte character = memory[MemoryStart + address];
                byte attribute = memory[MemoryStart + address + 1];

                uint foreground = Palette[attribute & 0x0F];
                uint background = Palette[blink ? (attribute >> 4) & 0x07 : attribute >> 4];

                bool hidden = blink && (attribute & 0x80) != 0 && !textOn;
                bool atCursor = cursorShown && cell == cursor;

                for (int line = 0; line < cellHeight; line++)
                {
                    byte bits = hidden ? (byte)0 : font[(character * cellHeight) + line];
                    if (atCursor && line >= firstLine && line <= lastLine)
                    {
                        bits = 0xFF;
                    }

                    int y = (row * cellHeight) + line;
                    var span = pixels.Slice((y * Width) + (column * cellWidth), cellWidth);
                    for (int x = 0; x < cellWidth; x++)
                    {
                        int bit = x * 8 / cellWidth;
                        span[x] = (bits & (0x80 >> bit)) != 0 ? foreground : background;
                    }
                }
            }
        }
    }

    private void RenderGraphics(Span<uint> pixels)
    {
        // the memory is in two banks: the even lines from 0, the odd from 2000h
        Span<uint> colors = stackalloc uint[4];
        bool high = Mode == 6;
        if (high)
        {
            colors[0] = Palette[0];
            colors[1] = Palette[ColorSelect & 0x0F];
        }
        else
        {
            int bright = (ColorSelect & 0x10) != 0 ? 8 : 0;
            bool cyan = (ColorSelect & 0x20) != 0 || Mode == 5;
            colors[0] = Palette[ColorSelect & 0x0F];
            colors[1] = Palette[(cyan ? 3 : 2) + bright];
            colors[2] = Palette[(cyan ? 5 : 4) + bright];
            colors[3] = Palette[(cyan ? 7 : 6) + bright];
        }

        int repeat = Height / 200;
        int start = StartAddress * 2;
        for (int y = 0; y < 200; y++)
        {
            int rowStart = start + ((y & 1) * 0x2000) + ((y >> 1) * 80);
            var line = pixels.Slice(y * repeat * Width, Width);
            for (int x = 0; x < Width; x++)
            {
                uint pixel;
                if (high)
                {
                    byte b = memory[MemoryStart + ((rowStart + (x >> 3)) & 0x3FFF)];
                    pixel = colors[(b >> (7 - (x & 7))) & 1];
                }
                else
                {
                    int px = x >> 1;
                    byte b = memory[MemoryStart + ((rowStart + (px >> 2)) & 0x3FFF)];
                    pixel = colors[(b >> (6 - ((px & 3) * 2))) & 3];
                }

                line[x] = pixel;
            }

            for (int r = 1; r < repeat; r++)
            {
                line.CopyTo(pixels.Slice(((y * repeat) + r) * Width, Width));
            }
        }
    }

    private static byte[] LoadFont(string name)
    {
        using var stream = typeof(VideoAdapter).Assembly.GetManifestResourceStream("Fonts/" + name)
            ?? throw new InvalidOperationException($"The font {name} isn't in the assembly.");
        var font = new byte[stream.Length];
        stream.ReadExactly(font);
        return font;
    }
}
