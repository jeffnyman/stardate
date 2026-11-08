using SDL;
using Stardate.Core;
using static SDL.SDL3;

namespace Stardate.Player.Sdl;

/// <summary>
/// The machine's sound, the PC speaker's, out of the default playback device: 16-bit mono at
/// <see cref="Machine.SampleRate"/>, queued a frame at a time. SDL converts it to whatever the
/// device wants, and puts the one channel in both speakers.
/// </summary>
internal sealed unsafe class SdlAudio : IDisposable
{
    private readonly SDL_AudioStream* _stream;

    /// <summary>Opens the device, or throws if there is none.</summary>
    public SdlAudio()
    {
        SdlException.ThrowIfFalse(SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO), "SDL_InitSubSystem");

        var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_S16LE, channels = 1, freq = Machine.SampleRate };
        _stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, 0);
        SdlException.ThrowIfNull(_stream, "SDL_OpenAudioDeviceStream");
        SdlException.ThrowIfFalse(SDL_ResumeAudioStreamDevice(_stream), "SDL_ResumeAudioStreamDevice");
    }

    /// <summary>The samples queued and not yet played.</summary>
    public int Queued => SDL_GetAudioStreamQueued(_stream) / sizeof(short);

    public void Queue(ReadOnlySpan<short> samples)
    {
        fixed (short* data = samples)
        {
            SDL_PutAudioStreamData(_stream, (nint)data, samples.Length * sizeof(short));
        }
    }

    /// <summary>Throws away what is queued, so sound picks up from now.</summary>
    public void Clear() => SDL_ClearAudioStream(_stream);

    public void Dispose()
    {
        SDL_DestroyAudioStream(_stream);
        SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
    }
}
