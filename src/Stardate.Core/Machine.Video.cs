using Stardate.Core.Devices;

namespace Stardate.Core;

// The video BIOS, INT 10h, as a color graphics adapter's: modes, the cursor, scrolling, and
// writing characters, in text and in the graphics modes. What it's asked for it keeps in the
// BIOS data area, as a real BIOS does, and sets the adapter's registers to match.
public sealed partial class Machine
{
    // the BIOS data area's video fields, at 0040h
    private const ushort ModeByte = 0x49, ColumnsWord = 0x4A, PageSizeWord = 0x4C, PageStartWord = 0x4E;
    private const ushort CursorPositions = 0x50, CursorTypeWord = 0x60, ActivePageByte = 0x62;
    private const ushort ModeControlByte = 0x65, PaletteByte = 0x66, RowsByte = 0x84, CharacterHeightWord = 0x85;

    private int Columns => ReadWord(0x40, ColumnsWord);

    private int ActivePage => ReadByte(0x40, ActivePageByte);

    private int PageSize => ReadWord(0x40, PageSizeWord);

    private bool IsTextMode => _video.IsText;

    /// <summary>INT 10h, the video BIOS.</summary>
    private void VideoService()
    {
        int bh = (byte)(Cpu.Ebx >> 8), bl = (byte)Cpu.Ebx;
        int ch = (byte)(Cpu.Ecx >> 8), cl = (byte)Cpu.Ecx, cx = (ushort)Cpu.Ecx;
        int dh = (byte)(Cpu.Edx >> 8), dl = (byte)Cpu.Edx;
        switch (Ah)
        {
            case 0x00:
                // bit 7 asks for the memory to be kept
                SetVideoMode(Al & 0x7F, clear: (Al & 0x80) == 0);
                break;
            case 0x01:
                WriteWord(0x40, CursorTypeWord, (ushort)((ch << 8) | cl));
                _video.CursorShape = ((byte)ch, (byte)cl);
                break;
            case 0x02:
                SetCursor(bh & 7, dh, dl);
                break;
            case 0x03:
            {
                var (row, column) = CursorOf(bh & 7);
                SetCx(ReadWord(0x40, CursorTypeWord));
                SetDx((row << 8) | column);
                break;
            }

            case 0x04:
                // the light pen: never triggered
                SetAh(0);
                break;
            case 0x05:
                SetActivePage(Al & 7);
                break;
            case 0x06:
            case 0x07:
                Scroll(Ah == 0x06, Al, bh, ch, cl, dh, dl);
                break;
            case 0x08:
            {
                var (row, column) = CursorOf(bh & 7);
                SetAx(IsTextMode ? ReadCell(bh & 7, row, column) : ReadGraphicsCharacter(row, column));
                break;
            }

            case 0x09:
            case 0x0A:
            {
                var (row, column) = CursorOf(bh & 7);
                for (int i = 0; i < cx; i++)
                {
                    // the characters run on along the row, and down, without moving the cursor
                    int at = column + i;
                    PutCharacter(bh & 7, row + (at / Columns), at % Columns, Al, Ah == 0x09 ? bl : null);
                }

                break;
            }

            case 0x0B:
                // the CGA's palette: BH 0 sets the background (or border), 1 picks the palette
                byte select = ReadByte(0x40, PaletteByte);
                select = bh == 0 ? (byte)((select & 0xE0) | (bl & 0x1F)) : (byte)((select & 0xDF) | ((bl & 1) << 5));
                WriteByte(0x40, PaletteByte, select);
                _video.ColorSelect = select;
                break;
            case 0x0C:
                PutPixel(cx, (ushort)Cpu.Edx, Al);
                break;
            case 0x0D:
                SetAl(GetPixel(cx, (ushort)Cpu.Edx));
                break;
            case 0x0E:
                Teletype(Al, bl);
                break;
            case 0x0F:
                SetAx((Columns << 8) | VideoMode);
                Cpu.Ebx = (Cpu.Ebx & 0xFFFF_00FF) | ((uint)ActivePage << 8);
                break;
            case 0x13:
                WriteString(bh & 7, bl, cx, dh, dl);
                break;
            default:
                // the EGA's and VGA's own functions (the palette registers, fonts, display
                // information): a CGA's BIOS doesn't have them, and leaves the registers as they
                // were, by which a program knows it's on a CGA
                Trace($"10/{Ah:X2} not a CGA function");
                break;
        }
    }

    /// <summary>Sets a video mode as the BIOS does: the data area, the adapter's registers, and the screen cleared.</summary>
    private void SetVideoMode(int mode, bool clear)
    {
        if (mode > 6)
        {
            // the monochrome adapter's mode, and the later cards': this is a CGA, which leaves
            // the mode it's in
            Trace($"10/00 set video mode {mode:X2}h: not a CGA mode");
            return;
        }

        _video.SetMode(mode);
        bool text = mode <= 3;
        int columns = mode is 0 or 1 or 4 or 5 ? 40 : 80;
        WriteByte(0x40, ModeByte, (byte)mode);
        WriteWord(0x40, ColumnsWord, (ushort)columns);
        WriteWord(0x40, PageSizeWord, (ushort)(text ? (columns == 40 ? 0x800 : 0x1000) : 0x4000));
        WriteWord(0x40, PageStartWord, 0);
        for (uint page = 0; page < 8; page++)
        {
            WriteWord(0x40, CursorPositions + (2 * page), 0);
        }

        WriteWord(0x40, CursorTypeWord, 0x0607);
        WriteByte(0x40, ActivePageByte, 0);
        WriteByte(0x40, ModeControlByte, _video.ModeControl);
        WriteByte(0x40, PaletteByte, _video.ColorSelect);
        WriteByte(0x40, RowsByte, VideoAdapter.Rows - 1);
        WriteWord(0x40, CharacterHeightWord, 8);
        _video.CursorAddress = 0;

        if (clear)
        {
            // text clears to spaces in light gray on black, graphics to the background
            var memory = _memory.AsSpan((int)VideoAdapter.MemoryStart, VideoAdapter.MemorySize);
            if (text)
            {
                for (int i = 0; i < memory.Length; i += 2)
                {
                    memory[i] = 0x20;
                    memory[i + 1] = 0x07;
                }
            }
            else
            {
                memory.Clear();
            }
        }

        Trace($"10/00 set video mode {mode:X2}h");
    }

    private (int Row, int Column) CursorOf(int page)
    {
        ushort position = ReadWord(0x40, CursorPositions + (2 * (uint)page));
        return (position >> 8, position & 0xFF);
    }

    private void SetCursor(int page, int row, int column)
    {
        WriteWord(0x40, CursorPositions + (2 * (uint)page), (ushort)((row << 8) | column));
        if (page == ActivePage)
        {
            // the adapter's cursor is an address in characters, from the memory's start
            _video.CursorAddress = (ReadWord(0x40, PageStartWord) / 2) + (row * Columns) + column;
        }
    }

    private void SetActivePage(int page)
    {
        if (!IsTextMode)
        {
            return;
        }

        int start = page * PageSize;
        WriteByte(0x40, ActivePageByte, (byte)page);
        WriteWord(0x40, PageStartWord, (ushort)start);
        _video.StartAddress = start / 2;
        var (row, column) = CursorOf(page);
        SetCursor(page, row, column);
    }

    /// <summary>A character cell's address in memory, as a linear address, in text modes.</summary>
    private uint CellAddress(int page, int row, int column) =>
        VideoAdapter.MemoryStart + (uint)((((page * PageSize) + (((row * Columns) + column) * 2))) & (VideoAdapter.MemorySize - 1));

    private ushort ReadCell(int page, int row, int column)
    {
        uint address = CellAddress(page, row, column);
        return (ushort)(_memory[address] | (_memory[address + 1] << 8));
    }

    /// <summary>Writes a character at a place, with an attribute or keeping the one there; in a graphics mode, draws it in a color.</summary>
    private void PutCharacter(int page, int row, int column, byte character, int? attribute)
    {
        if (row >= VideoAdapter.Rows)
        {
            return;
        }

        if (IsTextMode)
        {
            uint address = CellAddress(page, row, column);
            _memory[address] = character;
            if (attribute is { } a)
            {
                _memory[address + 1] = (byte)a;
            }
        }
        else
        {
            DrawGraphicsCharacter(row, column, character, attribute ?? 1);
        }
    }

    /// <summary>
    /// Scrolls a window of the active page up (or down) by some lines, filling those it opens
    /// with spaces in an attribute; no lines (or more than the window has) clears it.
    /// </summary>
    private void Scroll(bool up, int lines, int attribute, int top, int left, int bottom, int right)
    {
        right = Math.Min(right, Columns - 1);
        bottom = Math.Min(bottom, VideoAdapter.Rows - 1);
        if (top > bottom || left > right)
        {
            return;
        }

        int height = bottom - top + 1;
        if (lines == 0 || lines > height)
        {
            lines = height;
        }

        int page = ActivePage;
        for (int i = 0; i < height; i++)
        {
            int row = up ? top + i : bottom - i;
            int from = up ? row + lines : row - lines;
            bool fromInside = up ? from <= bottom : from >= top;
            for (int column = left; column <= right; column++)
            {
                if (IsTextMode)
                {
                    uint to = CellAddress(page, row, column);
                    if (fromInside)
                    {
                        uint source = CellAddress(page, from, column);
                        _memory[to] = _memory[source];
                        _memory[to + 1] = _memory[source + 1];
                    }
                    else
                    {
                        _memory[to] = 0x20;
                        _memory[to + 1] = (byte)attribute;
                    }
                }
                else
                {
                    CopyGraphicsCell(fromInside ? from : -1, row, column, attribute);
                }
            }
        }
    }

    /// <summary>
    /// Writes a character as a terminal would, at the active page's cursor, and moves the cursor
    /// on: a bell, backspace, carriage return and line feed do what they say, and the screen
    /// scrolls up from the bottom line. DOS writes its console this way.
    /// </summary>
    internal void Teletype(byte character, int graphicsColor = 1)
    {
        int page = ActivePage;
        var (row, column) = CursorOf(page);
        switch (character)
        {
            case 0x07:
                Beep();
                return;
            case 0x08:
                if (column > 0)
                {
                    column--;
                }

                break;
            case 0x0A:
                row++;
                break;
            case 0x0D:
                column = 0;
                break;
            default:
                PutCharacter(page, row, column, character, IsTextMode ? null : graphicsColor);
                if (++column >= Columns)
                {
                    column = 0;
                    row++;
                }

                break;
        }

        if (row >= VideoAdapter.Rows)
        {
            // the new line takes the attribute of the character the cursor was on
            int attribute = IsTextMode ? ReadCell(page, VideoAdapter.Rows - 1, 0) >> 8 : 0;
            Scroll(true, 1, attribute, 0, 0, VideoAdapter.Rows - 1, Columns - 1);
            row = VideoAdapter.Rows - 1;
        }

        SetCursor(page, row, column);
    }

    /// <summary>
    /// INT 10h function 13h: a string from ES:BP at a place. AL's bit 1 says the string is
    /// characters and attributes in turn, rather than characters in BL's attribute; bit 0 that
    /// the cursor is left after it.
    /// </summary>
    private void WriteString(int page, int attribute, int count, int row, int column)
    {
        int mode = Al;
        var (savedRow, savedColumn) = CursorOf(page);
        SetCursor(page, row, column);
        uint at = (ushort)Cpu.Ebp;
        for (int i = 0; i < count; i++)
        {
            byte character = ReadByte(Cpu.Es, at++);
            if ((mode & 2) != 0)
            {
                attribute = ReadByte(Cpu.Es, at++);
            }

            if (character is 0x07 or 0x08 or 0x0A or 0x0D)
            {
                Teletype(character);
                continue;
            }

            // a character goes in the attribute given, and the cursor on, as the teletype moves it
            var (r, c) = CursorOf(page);
            PutCharacter(page, r, c, character, attribute);
            if (++c >= Columns)
            {
                c = 0;
                if (++r >= VideoAdapter.Rows)
                {
                    Scroll(true, 1, IsTextMode ? attribute : 0, 0, 0, VideoAdapter.Rows - 1, Columns - 1);
                    r = VideoAdapter.Rows - 1;
                }
            }

            SetCursor(page, r, c);
        }

        if ((mode & 1) == 0)
        {
            SetCursor(page, savedRow, savedColumn);
        }
    }

    // ---- characters and pixels in the graphics modes -----------------------------------------

    // in the graphics modes the BIOS draws characters from the CGA's 8 by 8 font, in its ROM;
    // a row of text is eight lines, and a 320-pixel mode's character is 2 bits a pixel
    private static readonly byte[] GraphicsFont = LoadGraphicsFont();

    private static byte[] LoadGraphicsFont()
    {
        using var stream = typeof(Machine).Assembly.GetManifestResourceStream("Fonts/Font8x8.bin")!;
        var font = new byte[stream.Length];
        stream.ReadExactly(font);
        return font;
    }

    private int BitsPerPixel => VideoMode == 6 ? 1 : 2;

    /// <summary>The byte holding a pixel's line, in the CGA's two banks, by its row of 80 bytes.</summary>
    private static uint GraphicsLine(int y) => VideoAdapter.MemoryStart + (uint)(((y & 1) * 0x2000) + ((y >> 1) * 80));

    private void PutPixel(int x, int y, byte color)
    {
        if (IsTextMode || y >= 200)
        {
            return;
        }

        int bits = BitsPerPixel, perByte = 8 / bits;
        if (x >= 640 / bits)
        {
            return;
        }

        uint address = GraphicsLine(y) + (uint)(x / perByte);
        int shift = (perByte - 1 - (x % perByte)) * bits;
        int mask = ((1 << bits) - 1) << shift;
        int value = (color & ((1 << bits) - 1)) << shift;
        _memory[address] = (byte)((color & 0x80) != 0 ? _memory[address] ^ value : (_memory[address] & ~mask) | value);
    }

    private byte GetPixel(int x, int y)
    {
        if (IsTextMode || y >= 200)
        {
            return 0;
        }

        int bits = BitsPerPixel, perByte = 8 / bits;
        uint address = GraphicsLine(y) + (uint)(x / perByte);
        int shift = (perByte - 1 - (x % perByte)) * bits;
        return (byte)((_memory[address] >> shift) & ((1 << bits) - 1));
    }

    private void DrawGraphicsCharacter(int row, int column, byte character, int color)
    {
        for (int line = 0; line < 8; line++)
        {
            byte bits = GraphicsFont[(character * 8) + line];
            for (int x = 0; x < 8; x++)
            {
                bool on = (bits & (0x80 >> x)) != 0;
                int value = on ? color & 0x7F : 0;
                if ((color & 0x80) != 0)
                {
                    // bit 7 draws by XOR, leaving what's under the character's background
                    if (on)
                    {
                        PutPixel((column * 8) + x, (row * 8) + line, (byte)(0x80 | (color & 0x7F)));
                    }
                }
                else
                {
                    PutPixel((column * 8) + x, (row * 8) + line, (byte)value);
                }
            }
        }
    }

    private ushort ReadGraphicsCharacter(int row, int column)
    {
        // the character whose pattern matches the cell's lit pixels, as the BIOS found it
        Span<byte> pattern = stackalloc byte[8];
        for (int line = 0; line < 8; line++)
        {
            byte bits = 0;
            for (int x = 0; x < 8; x++)
            {
                if (GetPixel((column * 8) + x, (row * 8) + line) != 0)
                {
                    bits |= (byte)(0x80 >> x);
                }
            }

            pattern[line] = bits;
        }

        for (int c = 0; c < 256; c++)
        {
            if (GraphicsFont.AsSpan(c * 8, 8).SequenceEqual(pattern))
            {
                return (ushort)c;
            }
        }

        return 0;
    }

    /// <summary>Copies a character cell's pixels from another row (or, from row -1, fills it with a color), for scrolling in a graphics mode.</summary>
    private void CopyGraphicsCell(int fromRow, int toRow, int column, int fill)
    {
        int width = 8;
        for (int line = 0; line < 8; line++)
        {
            for (int x = 0; x < width; x++)
            {
                int px = (column * width) + x;
                byte value = fromRow < 0 ? (byte)(fill & 0x7F) : GetPixel(px, (fromRow * 8) + line);
                PutPixel(px, (toRow * 8) + line, value);
            }
        }
    }
}
