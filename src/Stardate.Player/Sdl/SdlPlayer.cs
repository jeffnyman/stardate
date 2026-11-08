using System.Diagnostics;
using SDL;
using Stardate.Core;
using static SDL.SDL3;

namespace Stardate.Player.Sdl;

internal sealed class PlayerOptions
{
    public string Title { get; init; } = "Stardate";

    /// <summary>Integer scale of the 640-wide picture for the window's first size; it can be resized afterwards.</summary>
    public int Scale { get; init; } = 2;

    public bool Fullscreen { get; init; }

    public bool SquarePixels { get; init; }

    /// <summary>Plays the machine's sound; when false, or when there's no sound device, the player is silent.</summary>
    public bool Sound { get; init; } = true;

    /// <summary>The file Ctrl+F5 saves the machine's state to and Ctrl+F9 loads it from; none, and they do nothing.</summary>
    public string? StatePath { get; init; }
}

/// <summary>
/// Plays the machine in a window, a frame at a time: a frame's worth of the machine's time
/// (1/70th of a second, the VGA's rate in its text modes and in the CGA's modes it doubles), then
/// the picture and the sound, then a wait for the next frame's moment. With sound, the moment is
/// the sound device's: the next frame runs when the sound queued has played down to a few frames,
/// so picture and sound never drift apart. Without, it's the host's clock. The keyboard is read
/// between frames.
/// </summary>
/// <remarks>
/// The machine's own sense of time comes from the instructions it runs, so the game always sees
/// the same pace; the player's job is only to hand it its instructions at the rate of real time.
/// If the host falls behind (the window was dragged, the machine was busy), the player carries on
/// from now rather than racing to catch up.
/// </remarks>
internal sealed unsafe class SdlPlayer : IDisposable
{
    // the VGA's frame rate in its 400-line modes, which the picture is shown at
    private const double FrameRate = 70.086;

    // at full speed, frames run between pictures for this long, in seconds of real time
    private const double WarpSlice = 1 / 60.0;

    // how long a message, such as "saved", shows in the title bar, in seconds
    private const double MessageSeconds = 2;

    // sound queued ahead of the device, in samples: 60 ms, about four frames, enough to ride out a late frame
    private const int SoundAhead = Machine.SampleRate * 60 / 1000;

    private readonly short[] _samples = new short[Machine.SampleRate / 10];
    private readonly SdlAudio? _audio;

    private readonly Machine _machine;
    private readonly PlayerOptions _options;
    private readonly SdlWindow _window;

    // the PC keys held down now, by scan code and whether extended, to let go when focus is lost
    private readonly HashSet<(byte Code, bool Extended)> _held = [];

    private bool _quit;
    private bool _paused;
    private bool _warp;
    private string? _message;
    private long _messageUntil;
    private long _frames;
    private long _instructions;

    public SdlPlayer(Machine machine, PlayerOptions options)
    {
        _machine = machine;
        _options = options;
        SdlException.ThrowIfFalse(SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_EVENTS), "SDL_Init");
        _window = new SdlWindow(options.Title, options.Scale, options.Fullscreen, options.SquarePixels, machine.DisplayWidth, machine.DisplayHeight);
        if (options.Sound)
        {
            try
            {
                _audio = new SdlAudio();
            }
            catch (SdlException ex)
            {
                Console.Error.WriteLine($"stardate: no sound: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Plays until the window is closed or the program ends, or for the emulated seconds given.
    /// Returns whether the program is still running.
    /// </summary>
    public bool Run(double? seconds = null)
    {
        long frameTicks = (long)(Stopwatch.Frequency / FrameRate);
        long next = Stopwatch.GetTimestamp();
        bool running = true;

        while (running && !_quit && (seconds is null || _machine.Time.TotalSeconds < seconds))
        {
            Pump();
            if (_paused)
            {
                // what's queued plays out, and the sound picks up again from the moment of unpausing
                _window.Redraw();
                SDL_Delay(16);
                next = Stopwatch.GetTimestamp();
                continue;
            }

            if (_warp)
            {
                // as many frames as fit in a slice of real time, then one picture
                long until = Stopwatch.GetTimestamp() + (long)(WarpSlice * Stopwatch.Frequency);
                do
                {
                    running = RunFrame();

                    // sound at full speed is no use: it's dropped
                    _machine.SkipSamples();
                }
                while (running && Stopwatch.GetTimestamp() < until);

                Present();
                next = Stopwatch.GetTimestamp();
                continue;
            }

            running = RunFrame();
            Present();

            if (_audio is not null)
            {
                for (int count; (count = _machine.ReadSamples(_samples)) > 0;)
                {
                    _audio.Queue(_samples.AsSpan(0, count));
                }

                while (_audio.Queued > SoundAhead && !_quit)
                {
                    SDL_DelayPrecise(1_000_000);
                }

                continue;
            }

            // without sound, nobody hears it
            _machine.SkipSamples();

            next += frameTicks;
            long wait = next - Stopwatch.GetTimestamp();
            if (wait > 0)
            {
                SDL_DelayPrecise((ulong)(wait * 1_000_000_000L / Stopwatch.Frequency));
            }
            else if (-wait > frameTicks * 5)
            {
                next = Stopwatch.GetTimestamp();
            }
        }

        return running;
    }

    public void Dispose()
    {
        _audio?.Dispose();
        _window.Dispose();
        SDL_Quit();
    }

    /// <summary>
    /// One frame of the machine's time. The instructions are counted from the start, so the
    /// fractions of an instruction a frame leaves over add up rather than being lost.
    /// </summary>
    private bool RunFrame()
    {
        _frames++;
        long due = (long)(_frames * (double)_machine.InstructionsPerSecond / FrameRate);
        bool running = _machine.Run(due - _instructions);
        _instructions = due;
        return running;
    }

    private void Present() => _window.Present(_machine.DisplayWidth, _machine.DisplayHeight, _machine.Render);

    private void Pump()
    {
        SDL_Event sdlEvent;
        while (SDL_PollEvent(&sdlEvent))
        {
            switch ((SDL_EventType)sdlEvent.type)
            {
                case SDL_EventType.SDL_EVENT_QUIT:
                case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                    _quit = true;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_RESIZED:
                case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                case SDL_EventType.SDL_EVENT_WINDOW_EXPOSED:
                    _window.Redraw();
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                    // the keys up go to another window: let go of every key held, or one (Alt,
                    // after Alt+Tab) stays down in the game
                    foreach (var (code, extended) in _held)
                    {
                        _machine.ReleaseKey(code, extended);
                    }

                    _held.Clear();
                    break;

                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                case SDL_EventType.SDL_EVENT_KEY_UP:
                    Key(sdlEvent.key);
                    break;
            }
        }

        if (_message is not null && Stopwatch.GetTimestamp() > _messageUntil)
        {
            _message = null;
            ShowStatus();
        }
    }

    private void Key(SDL_KeyboardEvent key)
    {
        // the player's own keys, which the game never sees: the games read neither F11 nor F12,
        // which came after them, and Pause is the one key a game can't read
        switch (key.scancode)
        {
            case SDL_Scancode.SDL_SCANCODE_F11:
                if (key.down && !key.repeat)
                {
                    if ((key.mod & SDL_Keymod.SDL_KMOD_SHIFT) != 0)
                    {
                        _window.ToggleSquarePixels();
                    }
                    else
                    {
                        _window.ToggleFullscreen();
                    }
                }

                return;
            case SDL_Scancode.SDL_SCANCODE_F12:
                if (key.down && !key.repeat)
                {
                    _warp = !_warp;
                    ShowStatus();
                }

                return;
            case SDL_Scancode.SDL_SCANCODE_F5 when (key.mod & SDL_Keymod.SDL_KMOD_CTRL) != 0:
                if (key.down && !key.repeat)
                {
                    SaveState();
                }

                return;
            case SDL_Scancode.SDL_SCANCODE_F9 when (key.mod & SDL_Keymod.SDL_KMOD_CTRL) != 0:
                if (key.down && !key.repeat)
                {
                    LoadState();
                }

                return;
            case SDL_Scancode.SDL_SCANCODE_PAUSE:
                if (key.down && !key.repeat)
                {
                    _paused = !_paused;
                    ShowStatus();
                }

                return;
        }

        if (!ScanCodes.TryFromUsb((int)key.scancode, out byte code, out bool extended))
        {
            return;
        }

        if (key.down)
        {
            // a key held down repeats its make code, as the PC's keyboard does by itself
            _held.Add((code, extended));
            _machine.PressKey(code, extended);
        }
        else if (_held.Remove((code, extended)))
        {
            _machine.ReleaseKey(code, extended);
        }
    }

    private void ShowStatus()
        => _window.ShowStatus(_message ?? (_paused ? "paused" : _warp ? "full speed" : null));

    /// <summary>Says what the player just did, in the title bar, for a couple of seconds.</summary>
    private void Message(string message)
    {
        _message = message;
        _messageUntil = Stopwatch.GetTimestamp() + (long)(MessageSeconds * Stopwatch.Frequency);
        ShowStatus();
    }

    /// <summary>Saves the whole machine to the state file: Ctrl+F5.</summary>
    private void SaveState()
    {
        if (_options.StatePath is not { } path)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // written aside and then moved, so a failed save leaves the last one whole
            string temporary = path + ".new";
            using (var file = File.Create(temporary))
            {
                _machine.SaveState(file);
            }

            File.Move(temporary, path, overwrite: true);
            Message("state saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message("state not saved");
            Console.Error.WriteLine($"stardate: could not save the state: {ex.Message}");
        }
    }

    /// <summary>Puts the machine back as the state file has it: Ctrl+F9.</summary>
    private void LoadState()
    {
        if (_options.StatePath is not { } path)
        {
            return;
        }

        if (!File.Exists(path))
        {
            Message("no state saved");
            return;
        }

        try
        {
            using (var file = File.OpenRead(path))
            {
                _machine.LoadState(file);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message("state not loaded");
            Console.Error.WriteLine($"stardate: could not load the state: {ex.Message}");
            return;
        }

        // the keys held when it was saved are let go, and the ones held now (Ctrl, at least)
        // pressed, so the game's idea of the keyboard is the keyboard's
        _machine.ReleaseKeys();
        foreach (var (code, extended) in _held)
        {
            _machine.PressKey(code, extended);
        }

        // the sound from the state's moment on
        _audio?.Clear();
        _machine.SkipSamples();

        Message("state loaded");
    }
}
