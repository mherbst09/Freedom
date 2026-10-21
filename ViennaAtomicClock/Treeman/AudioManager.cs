using System.Runtime.InteropServices;

namespace ViennaAtomicClock.Treeman;

public enum SoundEffect { Chop, SideSwitch, GameOver, Highscore, MenuSelect, Countdown, CaseTick, CaseReveal }

/// <summary>
/// Plays effects and music. All sounds are generated here (no audio files, no copyrighted material) and mixed
/// on a background thread into a waveOut stream, so effects can overlap and three volume controls work.
/// If no audio device exists, every call is silently ignored – the game never fails because of sound.
/// </summary>
public sealed class AudioManager : IDisposable
{
    public const int SampleRate = 22050;
    private const int BufferSamples = 512; // ~23 ms per buffer
    private const int BufferCount = 4;

    private readonly Dictionary<SoundEffect, float[]> _effects;
    private readonly float[] _music;
    private readonly object _gate = new();
    private readonly List<(float[] Data, int Position, float Gain)> _voices = new();
    private readonly float[] _mix = new float[BufferSamples];
    private int _musicPosition;
    private bool _musicPlaying;
    private volatile bool _running;
    private Thread? _thread;
    private IntPtr _device;

    public AudioManager(TreemanSaveData volumes)
    {
        Volumes = volumes;
        _effects = new Dictionary<SoundEffect, float[]>
        {
            [SoundEffect.Chop] = SoundSynth.Chop(),
            [SoundEffect.SideSwitch] = SoundSynth.Whoosh(),
            [SoundEffect.GameOver] = SoundSynth.GameOver(),
            [SoundEffect.Highscore] = SoundSynth.Fanfare(),
            [SoundEffect.MenuSelect] = SoundSynth.Blip(880, 0.06),
            [SoundEffect.Countdown] = SoundSynth.Blip(660, 0.09),
            [SoundEffect.CaseTick] = SoundSynth.Tick(),
            [SoundEffect.CaseReveal] = SoundSynth.Chime()
        };
        _music = SoundSynth.MusicLoop();
    }

    /// <summary>Volumes (0..1) from the save data; changes take effect immediately.</summary>
    public TreemanSaveData Volumes { get; }

    public bool Available => _device != IntPtr.Zero;

    /// <summary>Opens the audio device (once). Does nothing if there is none.</summary>
    public void Start()
    {
        if (_running) return;
        try
        {
            var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSec = SampleRate, BitsPerSample = 16, BlockAlign = 2, AvgBytesPerSec = SampleRate * 2 };
            if (waveOutOpen(out _device, -1, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0)
            {
                _device = IntPtr.Zero;
                return;
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            _device = IntPtr.Zero;
            return;
        }
        _running = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "Treeman audio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Play(SoundEffect effect, float gain = 1)
    {
        if (!_running || !_effects.TryGetValue(effect, out var data)) return;
        lock (_gate)
        {
            if (_voices.Count >= 16) _voices.RemoveAt(0); // cap simultaneous voices
            _voices.Add((data, 0, gain));
        }
    }

    public void SetMusic(bool playing)
    {
        lock (_gate) _musicPlaying = playing;
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        if (_device != IntPtr.Zero)
        {
            waveOutReset(_device);
            waveOutClose(_device);
            _device = IntPtr.Zero;
        }
    }

    /// <summary>Keeps a few buffers queued; each one mixes all active voices plus the music loop.</summary>
    private void Pump()
    {
        var headerSize = Marshal.SizeOf<WaveHeader>();
        var headers = new IntPtr[BufferCount];
        var data = new IntPtr[BufferCount];
        var pcm = new short[BufferSamples];
        for (var i = 0; i < BufferCount; i++)
        {
            data[i] = Marshal.AllocHGlobal(BufferSamples * 2);
            headers[i] = Marshal.AllocHGlobal(headerSize);
            var header = new WaveHeader { Data = data[i], BufferLength = BufferSamples * 2 };
            Marshal.StructureToPtr(header, headers[i], false);
            waveOutPrepareHeader(_device, headers[i], headerSize);
            Fill(pcm);
            Marshal.Copy(pcm, 0, data[i], BufferSamples);
            waveOutWrite(_device, headers[i], headerSize);
        }
        try
        {
            while (_running)
            {
                var queued = false;
                for (var i = 0; i < BufferCount; i++)
                {
                    var header = Marshal.PtrToStructure<WaveHeader>(headers[i]);
                    if ((header.Flags & 1) == 0) continue; // WHDR_DONE not set yet
                    Fill(pcm);
                    Marshal.Copy(pcm, 0, data[i], BufferSamples);
                    header.Flags &= ~1u;
                    Marshal.StructureToPtr(header, headers[i], false);
                    waveOutWrite(_device, headers[i], headerSize);
                    queued = true;
                }
                if (!queued) Thread.Sleep(4);
            }
        }
        finally
        {
            waveOutReset(_device);
            for (var i = 0; i < BufferCount; i++)
            {
                waveOutUnprepareHeader(_device, headers[i], headerSize);
                Marshal.FreeHGlobal(headers[i]);
                Marshal.FreeHGlobal(data[i]);
            }
        }
    }

    private void Fill(short[] pcm)
    {
        Array.Clear(_mix);
        var master = (float)Volumes.MasterVolume;
        var effects = (float)Volumes.EffectsVolume * master;
        var music = (float)Volumes.MusicVolume * master * 0.55f;
        lock (_gate)
        {
            for (var v = _voices.Count - 1; v >= 0; v--)
            {
                var (sound, position, gain) = _voices[v];
                var count = Math.Min(BufferSamples, sound.Length - position);
                for (var i = 0; i < count; i++) _mix[i] += sound[position + i] * gain * effects;
                position += count;
                if (position >= sound.Length) _voices.RemoveAt(v);
                else _voices[v] = (sound, position, gain);
            }
            if (_musicPlaying && music > 0)
                for (var i = 0; i < BufferSamples; i++)
                {
                    _mix[i] += _music[_musicPosition] * music;
                    _musicPosition = (_musicPosition + 1) % _music.Length;
                }
        }
        for (var i = 0; i < BufferSamples; i++)
        {
            var s = _mix[i];
            s = s / (1 + Math.Abs(s) * 0.25f); // soft limiter instead of hard clipping
            pcm[i] = (short)Math.Clamp(s * 30000, short.MinValue, short.MaxValue);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public int Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr device, int id, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr device);
}

/// <summary>Procedural sounds (all original, built from noise and simple waves).</summary>
internal static class SoundSynth
{
    private const int Rate = AudioManager.SampleRate;

    private static float[] Make(double seconds, Func<double, double> sample)
    {
        var data = new float[(int)(seconds * Rate)];
        for (var i = 0; i < data.Length; i++) data[i] = (float)sample(i / (double)Rate);
        return data;
    }

    /// <summary>Wooden "tock": filtered noise crack plus a short low knock.</summary>
    public static float[] Chop()
    {
        var random = new Random(3);
        double low = 0;
        return Make(0.16, t =>
        {
            var noise = random.NextDouble() * 2 - 1;
            low += (noise - low) * 0.35; // simple low-pass for a woody colour
            var crack = low * Math.Exp(-t * 45) * 0.9;
            var knock = Math.Sin(2 * Math.PI * (150 - t * 300) * t) * Math.Exp(-t * 28) * 0.7;
            return crack + knock;
        });
    }

    public static float[] Whoosh()
    {
        var random = new Random(5);
        double filtered = 0;
        return Make(0.09, t =>
        {
            filtered += ((random.NextDouble() * 2 - 1) - filtered) * 0.15;
            return filtered * Math.Sin(Math.PI * t / 0.09) * 0.5;
        });
    }

    public static float[] GameOver() => Make(0.75, t =>
    {
        var frequency = 330 * Math.Pow(0.35, t / 0.75);
        var tone = Math.Sign(Math.Sin(2 * Math.PI * frequency * t)) * 0.18 + Math.Sin(2 * Math.PI * frequency * t) * 0.3;
        var thud = Math.Sin(2 * Math.PI * 70 * t) * Math.Exp(-t * 9) * 0.6;
        return tone * Math.Exp(-t * 2.2) + thud;
    });

    public static float[] Fanfare()
    {
        double[] notes = { 523.25, 659.25, 783.99, 1046.5 };
        return Make(0.9, t =>
        {
            var index = Math.Min(3, (int)(t / 0.13));
            var local = t - index * 0.13;
            var f = notes[index];
            var envelope = Math.Exp(-local * (index == 3 ? 3 : 9));
            return (Math.Sin(2 * Math.PI * f * t) * 0.45 + Math.Sin(4 * Math.PI * f * t) * 0.12) * envelope;
        });
    }

    /// <summary>Short dry click for every card passing the marker.</summary>
    public static float[] Tick() => Make(0.03, t => (Math.Sin(2 * Math.PI * 2400 * t) * 0.5 + Math.Sin(2 * Math.PI * 900 * t) * 0.3) * Math.Exp(-t * 160));

    /// <summary>Bright two-note chime when the box result appears.</summary>
    public static float[] Chime() => Make(0.5, t =>
    {
        var f = t < 0.09 ? 987.77 : 1318.5;
        var local = t < 0.09 ? t : t - 0.09;
        return (Math.Sin(2 * Math.PI * f * t) * 0.4 + Math.Sin(6 * Math.PI * f * t) * 0.06) * Math.Exp(-local * 7);
    });

    public static float[] Blip(double frequency, double seconds) =>
        Make(seconds, t => Math.Sin(2 * Math.PI * frequency * t) * Math.Exp(-t * 30) * 0.45);

    /// <summary>A calm pentatonic loop (bass + plucked melody), about nine seconds long.</summary>
    public static float[] MusicLoop()
    {
        const double bpm = 104;
        var beat = 60 / bpm;
        double[] bass = { 110, 110, 146.83, 130.81 };
        int[] melody = { 0, 2, 4, 2, 5, 4, 2, 0, 4, 5, 7, 5, 4, 2, 0, -1 };
        double[] scale = { 440, 493.88, 554.37, 659.25, 739.99, 880, 987.77, 1108.73 };
        var length = beat * 16;
        return Make(length, t =>
        {
            var bar = (int)(t / (beat * 4)) % 4;
            var beatIndex = (int)(t / beat);
            var inBeat = t - beatIndex * beat;
            var b = Math.Sin(2 * Math.PI * bass[bar] * t) * 0.22 * (0.6 + 0.4 * Math.Exp(-inBeat * 4));
            var note = melody[beatIndex % melody.Length];
            var m = note < 0 ? 0 : Math.Sin(2 * Math.PI * scale[note] * t) * Math.Exp(-inBeat * 6) * 0.16;
            return b + m;
        });
    }
}
