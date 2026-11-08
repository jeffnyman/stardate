using SDL;
using static SDL.SDL3;

namespace Stardate.Player.Sdl;

/// <summary>
/// The window: the machine's picture in a texture of 32-bit pixels, which SDL's renderer scales
/// onto the window, on the graphics card where there is one. The window can be resized and
/// switched to full screen; the picture keeps its shape, centred with black around it.
/// </summary>
/// <remarks>
/// The machine's picture is 640 pixels across, and 400 down (the VGA's text, 8 by 16 a
/// character) or 200 (the CGA's, 8 by 8). Either filled a 4:3 monitor, so the pixels weren't
/// square: the picture is shown at 4:3 whatever its size in pixels, unless square pixels are
/// asked for. The size can change as the machine's font changes, and the texture follows it.
/// <para>
/// The renderer scales with the nearest pixel, which keeps the text's edges sharp; at a size that
/// isn't a whole number of the picture's pixels, some rows and columns are a pixel wider than
/// others, which a full screen hides well enough.
/// </para>
/// </remarks>
internal sealed unsafe class SdlWindow : IDisposable
{
    private const float MonitorAspect = 4f / 3f;

    // room left for the title bar and the system's own bars when the window's first size is chosen
    private const int Margin = 64;

    private readonly string _title;
    private SDL_Window* _window;
    private SDL_Renderer* _renderer;
    private SDL_Texture* _frame;
    private int _width, _height;

    /// <summary>
    /// Opens a window for a picture of <paramref name="width"/> by <paramref name="height"/>,
    /// <paramref name="scale"/> times its size, or as many times as the screen has room for.
    /// </summary>
    public SdlWindow(string title, int scale, bool fullscreen, bool squarePixels, int width, int height)
    {
        _title = title;
        SquarePixels = squarePixels;
        var flags = SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
        if (fullscreen)
        {
            flags |= SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;
        }

        // the picture's shape at one times, and as many times that as fit on the screen
        int baseHeight = squarePixels ? height : (int)(width / MonitorAspect);
        scale = Math.Max(1, scale);
        SDL_Rect usable;
        if (SDL_GetDisplayUsableBounds(SDL_GetPrimaryDisplay(), &usable))
        {
            while (scale > 1 && (width * scale > usable.w || (baseHeight * scale) + Margin > usable.h))
            {
                scale--;
            }
        }

        _window = SdlException.ThrowIfNull(SDL_CreateWindow(title, width * scale, baseHeight * scale, flags), "SDL_CreateWindow");
        SdlException.ThrowIfFalse(SDL_SetWindowMinimumSize(_window, 320, 200), "SDL_SetWindowMinimumSize");

        // the best the system has; SDL falls back on its own software renderer
        _renderer = SdlException.ThrowIfNull(SDL_CreateRenderer(_window, (string?)null), "SDL_CreateRenderer");
        Resize(width, height);
    }

    public bool IsFullscreen => (SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;

    /// <summary>Shows the picture's pixels square, rather than in the 4:3 shape a monitor gave them.</summary>
    public bool SquarePixels { get; private set; }

    /// <summary>
    /// Copies a picture into the window: <paramref name="width"/> by <paramref name="height"/>
    /// pixels of ARGB, row by row, drawn by <paramref name="render"/> straight into the texture.
    /// </summary>
    public void Present(int width, int height, RenderAction render)
    {
        if (width != _width || height != _height)
        {
            Resize(width, height);
        }

        nint pixelsAt;
        int pitchBytes;
        SdlException.ThrowIfFalse(SDL_LockTexture(_frame, null, &pixelsAt, &pitchBytes), "SDL_LockTexture");
        try
        {
            // the texture's rows can be padded; the machine draws unpadded rows, so pad them here
            int pitch = pitchBytes / sizeof(uint);
            var pixels = new Span<uint>((void*)pixelsAt, pitch * height);
            if (pitch == width)
            {
                render(pixels[..(width * height)]);
            }
            else
            {
                uint[] rows = new uint[width * height];
                render(rows);
                for (int y = 0; y < height; y++)
                {
                    rows.AsSpan(y * width, width).CopyTo(pixels.Slice(y * pitch, width));
                }
            }
        }
        finally
        {
            SDL_UnlockTexture(_frame);
        }

        Redraw();
    }

    /// <summary>Paints the last picture onto the window again, scaled to fit.</summary>
    public void Redraw()
    {
        int windowWidth, windowHeight;
        if (!SDL_GetCurrentRenderOutputSize(_renderer, &windowWidth, &windowHeight) || windowWidth <= 0 || windowHeight <= 0)
        {
            // minimized, on some systems
            return;
        }

        var target = Fit(windowWidth, windowHeight);
        SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 255);
        SDL_RenderClear(_renderer);
        SdlException.ThrowIfFalse(SDL_RenderTexture(_renderer, _frame, null, &target), "SDL_RenderTexture");
        SDL_RenderPresent(_renderer);
    }

    /// <summary>Switches between a window and the full screen.</summary>
    public void ToggleFullscreen() => SdlException.ThrowIfFalse(SDL_SetWindowFullscreen(_window, !IsFullscreen), "SDL_SetWindowFullscreen");

    /// <summary>Switches between square pixels and the monitor's shape, and repaints.</summary>
    public void ToggleSquarePixels()
    {
        SquarePixels = !SquarePixels;
        Redraw();
    }

    /// <summary>Shows a short status, such as "paused", after the title in the title bar; null clears it.</summary>
    public void ShowStatus(string? status) => SDL_SetWindowTitle(_window, status is null ? _title : $"{_title}  ({status})");

    public void Dispose()
    {
        if (_frame is not null)
        {
            SDL_DestroyTexture(_frame);
            _frame = null;
        }

        if (_renderer is not null)
        {
            SDL_DestroyRenderer(_renderer);
            _renderer = null;
        }

        if (_window is not null)
        {
            SDL_DestroyWindow(_window);
            _window = null;
        }
    }

    private void Resize(int width, int height)
    {
        if (_frame is not null)
        {
            SDL_DestroyTexture(_frame);
        }

        _frame = SdlException.ThrowIfNull(
            SDL_CreateTexture(_renderer, SDL_PixelFormat.SDL_PIXELFORMAT_ARGB8888, SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING, width, height),
            "SDL_CreateTexture");
        SdlException.ThrowIfFalse(SDL_SetTextureScaleMode(_frame, SDL_ScaleMode.SDL_SCALEMODE_NEAREST), "SDL_SetTextureScaleMode");
        _width = width;
        _height = height;
    }

    /// <summary>The largest rectangle of the picture's shape that fits the window, centred.</summary>
    private SDL_FRect Fit(int windowWidth, int windowHeight)
    {
        float aspect = SquarePixels ? (float)_width / _height : MonitorAspect;
        float scale = Math.Min(windowWidth / aspect, windowHeight);
        int w = Math.Max(1, (int)(aspect * scale));
        int h = Math.Max(1, (int)scale);
        return new SDL_FRect { x = (windowWidth - w) / 2, y = (windowHeight - h) / 2, w = w, h = h };
    }
}

/// <summary>Draws a picture into the pixels given, row by row.</summary>
internal delegate void RenderAction(Span<uint> pixels);
