namespace Stardate.Core.Devices;

/// <summary>
/// The VGA: 256 KB of video memory in four planes, the registers that decide how the processor
/// reaches that memory and how it's shown, and the DAC, the palette of 256 colors each pixel
/// indexes. Only the graphics modes with 256 colors are drawn: mode 13h, and the unchained
/// variants of it known as Mode X.
/// </summary>
/// <remarks>
/// <para>
/// Video memory is four 64 KB planes side by side, stored here interleaved, plane by plane
/// within each address, as DOSBox stores it. How a processor address reaches it depends on the
/// sequencer's memory mode. In mode 13h, "chain 4" is on: the low two bits of the address pick
/// the plane, so the processor sees one linear 64,000-byte screen, a byte per pixel. With chain 4
/// off (Mode X), an address names the same byte in all four planes, the map mask register picks
/// which planes a write reaches, and the screen shows four neighbouring pixels from the four
/// planes at each address. That gives a program four times the memory to draw in, room for pages
/// to flip between.
/// </para>
/// <para>
/// The graphics controller's write modes, logical operations and bit mask apply to unchained
/// writes, and its latches (a byte from each plane, loaded by every read) let a program copy
/// four pixels with one read and one write.
/// </para>
/// </remarks>
internal sealed class Vga
{
    /// <summary>The refresh rate of the 400-line modes, mode 13h's included.</summary>
    public const double FrameRate = 70.086;

    private const int TotalLines = 449;
    private const int VisibleLines = 400;

    private readonly byte[] _memory = new byte[0x4_0000];
    private readonly byte[] _text = new byte[0x8000];
    private readonly byte[] _dac = new byte[768];

    private readonly byte[] _sequencer = new byte[5];
    private readonly byte[] _graphics = new byte[9];
    private readonly byte[] _crtc = new byte[25];
    private readonly byte[] _attribute = new byte[21];
    private int _sequencerIndex;
    private int _graphicsIndex;
    private int _crtcIndex;
    private int _attributeIndex;
    private bool _attributeData;
    private byte _miscellaneous;

    // the DAC's ports: an index to write at and to read from, each with the color component next
    private int _dacWrite;
    private int _dacRead;
    private int _dacComponent;
    private byte _pelMask = 0xFF;

    private uint _latches;

    public Vga() => SetMode(3, clear: true);

    /// <summary>The time in seconds, set by the machine before each access, from which the retrace is timed.</summary>
    public double Time { get; set; }

    public int Mode { get; private set; }

    private bool Chained => (_sequencer[4] & 0x08) != 0;

    // ---- memory ------------------------------------------------------------------------------

    /// <summary>A read from the processor's window at A0000h (offsets below 10000h) or the text buffer at B8000h.</summary>
    public byte Read(uint address)
    {
        if (address >= 0xB_8000)
        {
            return _text[address & 0x7FFF];
        }

        uint offset = address & 0xFFFF;
        if (Chained)
        {
            return _memory[offset];
        }

        // every read loads the latches with a byte from each plane
        int at = (int)offset * 4;
        _latches = (uint)(_memory[at] | (_memory[at + 1] << 8) | (_memory[at + 2] << 16) | (_memory[at + 3] << 24));
        if ((_graphics[5] & 0x08) != 0)
        {
            // read mode 1: a bit set for each pixel whose color matches the compare register
            int result = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int color = 0;
                for (int plane = 0; plane < 4; plane++)
                {
                    color |= ((_memory[at + plane] >> bit) & 1) << plane;
                }

                int care = _graphics[7] & 0x0F;
                if ((color & care) == (_graphics[2] & care))
                {
                    result |= 1 << bit;
                }
            }

            return (byte)result;
        }

        return _memory[at + (_graphics[4] & 3)];
    }

    public void Write(uint address, byte value)
    {
        if (address >= 0xB_8000)
        {
            _text[address & 0x7FFF] = value;
            return;
        }

        uint offset = address & 0xFFFF;
        if (Chained)
        {
            _memory[offset] = value;
            return;
        }

        int at = (int)offset * 4;
        int mapMask = _sequencer[2] & 0x0F;
        int writeMode = _graphics[5] & 3;
        if (writeMode == 1)
        {
            // write mode 1: the latches, as the last read loaded them
            for (int plane = 0; plane < 4; plane++)
            {
                if ((mapMask & (1 << plane)) != 0)
                {
                    _memory[at + plane] = (byte)(_latches >> (8 * plane));
                }
            }

            return;
        }

        int rotate = _graphics[3] & 7;
        int rotated = ((value >> rotate) | (value << (8 - rotate))) & 0xFF;
        int bitMask = _graphics[8];
        if (writeMode == 3)
        {
            bitMask &= rotated;
        }

        for (int plane = 0; plane < 4; plane++)
        {
            if ((mapMask & (1 << plane)) == 0)
            {
                continue;
            }

            int data = writeMode switch
            {
                2 => (value & (1 << plane)) != 0 ? 0xFF : 0x00,
                3 => (_graphics[0] & (1 << plane)) != 0 ? 0xFF : 0x00,
                _ => (_graphics[1] & (1 << plane)) != 0
                    ? ((_graphics[0] & (1 << plane)) != 0 ? 0xFF : 0x00)
                    : rotated,
            };

            int latch = (byte)(_latches >> (8 * plane));
            data = ((_graphics[3] >> 3) & 3) switch
            {
                1 => data & latch,
                2 => data | latch,
                3 => data ^ latch,
                _ => data,
            };

            _memory[at + plane] = (byte)((data & bitMask) | (latch & ~bitMask));
        }
    }

    // ---- ports -------------------------------------------------------------------------------

    public byte In(int port)
    {
        switch (port)
        {
            case 0x3C1: return _attribute[_attributeIndex & 0x1F];
            case 0x3C4: return (byte)_sequencerIndex;
            case 0x3C5: return _sequencerIndex < _sequencer.Length ? _sequencer[_sequencerIndex] : (byte)0;
            case 0x3C6: return _pelMask;
            case 0x3C7: return 0x03;
            case 0x3C8: return (byte)_dacWrite;
            case 0x3C9:
            {
                byte value = _dac[(_dacRead * 3) + _dacComponent];
                if (++_dacComponent == 3)
                {
                    _dacComponent = 0;
                    _dacRead = (_dacRead + 1) & 0xFF;
                }

                return value;
            }

            case 0x3CC: return _miscellaneous;
            case 0x3CE: return (byte)_graphicsIndex;
            case 0x3CF: return _graphicsIndex < _graphics.Length ? _graphics[_graphicsIndex] : (byte)0;
            case 0x3D4: return (byte)_crtcIndex;
            case 0x3D5: return _crtcIndex < _crtc.Length ? _crtc[_crtcIndex] : (byte)0;
            case 0x3DA:
                // reading the status also resets the attribute controller to expect an index
                _attributeData = false;
                return Status();
            default: return 0xFF;
        }
    }

    public void Out(int port, byte value)
    {
        switch (port)
        {
            case 0x3C0:
                if (_attributeData)
                {
                    if ((_attributeIndex & 0x1F) < _attribute.Length)
                    {
                        _attribute[_attributeIndex & 0x1F] = value;
                    }
                }
                else
                {
                    _attributeIndex = value;
                }

                _attributeData = !_attributeData;
                break;
            case 0x3C2: _miscellaneous = value; break;
            case 0x3C4: _sequencerIndex = value & 7; break;
            case 0x3C5:
                if (_sequencerIndex < _sequencer.Length)
                {
                    _sequencer[_sequencerIndex] = value;
                }

                break;
            case 0x3C6: _pelMask = value; break;
            case 0x3C7:
                _dacRead = value;
                _dacComponent = 0;
                break;
            case 0x3C8:
                _dacWrite = value;
                _dacComponent = 0;
                break;
            case 0x3C9:
                // a color is three writes, red, green and blue, each of 6 bits
                _dac[(_dacWrite * 3) + _dacComponent] = (byte)(value & 0x3F);
                if (++_dacComponent == 3)
                {
                    _dacComponent = 0;
                    _dacWrite = (_dacWrite + 1) & 0xFF;
                }

                break;
            case 0x3CE: _graphicsIndex = value & 0x0F; break;
            case 0x3CF:
                if (_graphicsIndex < _graphics.Length)
                {
                    _graphics[_graphicsIndex] = value;
                }

                break;
            case 0x3D4: _crtcIndex = value & 0x1F; break;
            case 0x3D5:
                if (_crtcIndex < _crtc.Length)
                {
                    _crtc[_crtcIndex] = value;
                }

                break;
        }
    }

    /// <summary>
    /// Input status 1, port 3DAh. Bit 3 is set during the vertical retrace, when the beam returns to
    /// the top of the screen, about once every 14 ms; programs wait for it to change the picture
    /// without tearing. Bit 0 is set whenever the beam isn't drawing, in either retrace.
    /// </summary>
    private byte Status()
    {
        double frame = Time * FrameRate;
        double line = (frame - Math.Floor(frame)) * TotalLines;
        bool vertical = line >= 412 && line < 414;
        bool blank = line >= VisibleLines || (line - Math.Floor(line)) > 0.8;
        return (byte)((vertical ? 0x08 : 0) | (blank ? 0x01 : 0));
    }

    // ---- the palette, for the BIOS ------------------------------------------------------------

    public void SetColor(int index, int red, int green, int blue)
    {
        _dac[index * 3] = (byte)(red & 0x3F);
        _dac[(index * 3) + 1] = (byte)(green & 0x3F);
        _dac[(index * 3) + 2] = (byte)(blue & 0x3F);
    }

    public (byte Red, byte Green, byte Blue) GetColor(int index) =>
        (_dac[index * 3], _dac[(index * 3) + 1], _dac[(index * 3) + 2]);

    // ---- modes -------------------------------------------------------------------------------

    /// <summary>
    /// Sets a mode as the video BIOS does: its register values, the default palette, and,
    /// unless asked not to, cleared memory. Modes other than 13h are set up as 80 by 25 text.
    /// </summary>
    public void SetMode(int mode, bool clear)
    {
        Mode = mode;
        Array.Clear(_sequencer);
        Array.Clear(_graphics);
        Array.Clear(_crtc);
        Array.Clear(_attribute);

        if (mode == 0x13)
        {
            // the values of IBM's VGA BIOS for mode 13h
            byte[] sequencer = [0x03, 0x01, 0x0F, 0x00, 0x0E];
            byte[] crtc =
            [
                0x5F, 0x4F, 0x50, 0x82, 0x54, 0x80, 0xBF, 0x1F, 0x00, 0x41, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x9C, 0x8E, 0x8F, 0x28, 0x40, 0x96, 0xB9, 0xA3, 0xFF,
            ];
            byte[] graphics = [0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x05, 0x0F, 0xFF];
            sequencer.CopyTo(_sequencer, 0);
            crtc.CopyTo(_crtc, 0);
            graphics.CopyTo(_graphics, 0);
            _miscellaneous = 0x63;
        }
        else
        {
            byte[] sequencer = [0x03, 0x00, 0x03, 0x00, 0x02];
            sequencer.CopyTo(_sequencer, 0);
            _crtc[0x01] = 0x4F;
            _crtc[0x09] = 0x0F;
            _crtc[0x12] = 0x8F;
            _crtc[0x13] = 0x28;
            _graphics[8] = 0xFF;
            _miscellaneous = 0x67;
        }

        for (int i = 0; i < 16; i++)
        {
            _attribute[i] = (byte)i;
        }

        _attribute[0x10] = (byte)(mode == 0x13 ? 0x41 : 0x0C);
        _attribute[0x12] = 0x0F;
        _pelMask = 0xFF;
        DefaultPalette();

        if (clear)
        {
            Array.Clear(_memory);
            for (int i = 0; i < _text.Length; i += 2)
            {
                _text[i] = (byte)' ';
                _text[i + 1] = 0x07;
            }
        }
    }

    /// <summary>
    /// The default palette: the 16 colors of the EGA, then a ramp of 16 grays. The rest of the
    /// VGA BIOS's default (hue ramps) is left black: games set their own palettes before drawing.
    /// </summary>
    private void DefaultPalette()
    {
        Array.Clear(_dac);
        for (int i = 0; i < 16; i++)
        {
            int low = (i & 8) != 0 ? 0x15 : 0;
            int red = ((i & 4) != 0 ? 0x2A : 0) + low;
            int green = ((i & 2) != 0 ? 0x2A : 0) + low;
            int blue = ((i & 1) != 0 ? 0x2A : 0) + low;
            if (i == 6)
            {
                green = 0x15; // brown, not dark yellow
            }

            SetColor(i, red, green, blue);
        }

        int[] grays = [0, 5, 8, 11, 14, 17, 20, 24, 28, 32, 36, 40, 45, 50, 56, 63];
        for (int i = 0; i < 16; i++)
        {
            SetColor(16 + i, grays[i], grays[i], grays[i]);
        }
    }

    // ---- drawing -----------------------------------------------------------------------------

    /// <summary>
    /// The size of the picture, in pixels, as the CRT controller's registers give it: the
    /// horizontal display end in 4-pixel units, and the vertical display end divided by how many
    /// times each line is scanned. In text modes, 640 by 400.
    /// </summary>
    public (int Width, int Height) Size
    {
        get
        {
            if (Mode != 0x13)
            {
                return (640, 400);
            }

            int width = (_crtc[0x01] + 1) * 4;
            int end = _crtc[0x12] | ((_crtc[0x07] & 0x02) << 7) | ((_crtc[0x07] & 0x40) << 3);
            int scans = (_crtc[0x09] & 0x1F) + 1;
            if ((_crtc[0x09] & 0x80) != 0)
            {
                scans *= 2;
            }

            return (width, Math.Max(1, (end + 1) / scans));
        }
    }

    /// <summary>Draws the picture as 32-bit ARGB pixels. Text modes are drawn black for now.</summary>
    public void Render(Span<uint> pixels)
    {
        var (width, height) = Size;
        if (Mode != 0x13)
        {
            pixels[..(width * height)].Fill(0xFF00_0000);
            return;
        }

        Span<uint> palette = stackalloc uint[256];
        for (int i = 0; i < 256; i++)
        {
            static uint Eight(byte six) => (uint)((six << 2) | (six >> 4));
            int c = (i & _pelMask) * 3;
            palette[i] = 0xFF00_0000 | (Eight(_dac[c]) << 16) | (Eight(_dac[c + 1]) << 8) | Eight(_dac[c + 2]);
        }

        int start = (_crtc[0x0C] << 8) | _crtc[0x0D];
        int offset = _crtc[0x13] * 2;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = Chained
                    ? ((start * 4) + (y * offset * 4) + x) & 0x3_FFFF
                    : ((((start + (y * offset) + (x >> 2)) & 0xFFFF) * 4) + (x & 3));
                pixels[(y * width) + x] = palette[_memory[index]];
            }
        }
    }
}
