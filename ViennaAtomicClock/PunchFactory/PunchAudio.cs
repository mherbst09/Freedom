using System.Runtime.InteropServices;

namespace ViennaAtomicClock.PunchFactory;

public enum PunchSound { Light, Medium, Heavy, Crit, Fury, Buy, Upgrade, Achievement, EventSpawn, EventCatch, Ability, Prestige, Denied, Offline }

/// <summary>
/// All sounds are generated here (original, no files). A small mixer on a background thread plays overlapping
/// voices with their own pitch (combos rise in pitch) plus a music loop per studio; waveOut, 22 kHz mono.
/// Without an audio device every call does nothing.
/// </summary>
public sealed class PunchAudio : IDisposable
{
    public const int Rate = 22050;
    private const int BufferSamples = 512;
    private const int BufferCount = 4;

    private readonly Dictionary<PunchSound, float[]> _sounds;
    private readonly object _gate = new();
    private readonly List<Voice> _voices = new();
    private readonly float[] _mix = new float[BufferSamples];
    private float[] _music = Array.Empty<float>();
    private float[] _furyMusic = Array.Empty<float>();
    private double _musicPosition;
    private bool _musicOn;
    private bool _fury;
    private volatile bool _running;
    private Thread? _thread;
    private IntPtr _device;

    private sealed class Voice
    {
        public required float[] Data;
        public double Position;
        public double Step;
        public float Gain;
    }

    public PunchAudio(PunchSettings settings)
    {
        Settings = settings;
        _sounds = new Dictionary<PunchSound, float[]>
        {
            [PunchSound.Light] = Synth.Punch(0.7, 7),
            [PunchSound.Medium] = Synth.Punch(1.0, 11),
            [PunchSound.Heavy] = Synth.Punch(1.4, 17),
            [PunchSound.Crit] = Synth.Crit(),
            [PunchSound.Fury] = Synth.FuryStart(),
            [PunchSound.Buy] = Synth.Coin(),
            [PunchSound.Upgrade] = Synth.Arpeggio(new[] { 659.25, 783.99, 1046.5 }, 0.07),
            [PunchSound.Achievement] = Synth.Arpeggio(new[] { 523.25, 659.25, 783.99, 1046.5, 1318.5 }, 0.09),
            [PunchSound.EventSpawn] = Synth.Sparkle(),
            [PunchSound.EventCatch] = Synth.Arpeggio(new[] { 880, 1174.66, 1760 }, 0.05),
            [PunchSound.Ability] = Synth.Whoosh(),
            [PunchSound.Prestige] = Synth.Swell(),
            [PunchSound.Denied] = Synth.Buzz(),
            [PunchSound.Offline] = Synth.Arpeggio(new[] { 392, 523.25, 659.25 }, 0.12)
        };
    }

    public PunchSettings Settings { get; }
    public bool Available => _device != IntPtr.Zero;

    public void Start()
    {
        if (_running) return;
        try
        {
            var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSec = Rate, BitsPerSample = 16, BlockAlign = 2, AvgBytesPerSec = Rate * 2 };
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
        _thread = new Thread(Pump) { IsBackground = true, Name = "Punch Factory audio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Plays a sound; <paramref name="pitch"/> 1 = normal (higher in long combos).</summary>
    public void Play(PunchSound sound, double pitch = 1, float gain = 1)
    {
        if (!_running) return;
        var data = _sounds[sound];
        lock (_gate)
        {
            if (_voices.Count >= 24) _voices.RemoveAt(0);
            _voices.Add(new Voice { Data = data, Step = Math.Clamp(pitch, 0.25, 4), Gain = gain });
        }
    }

    /// <summary>Music of the given studio (0 = garage … 7 = orbit). Generated once per studio change.</summary>
    public void SetEnvironment(int index)
    {
        var music = Synth.Music(index, fury: false);
        var fury = Synth.Music(index, fury: true);
        lock (_gate)
        {
            _music = music;
            _furyMusic = fury;
            _musicPosition = 0;
        }
    }

    public void SetMusic(bool on)
    {
        lock (_gate) _musicOn = on;
    }

    public void SetFury(bool fury)
    {
        lock (_gate) _fury = fury;
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
            Marshal.StructureToPtr(new WaveHeader { Data = data[i], BufferLength = BufferSamples * 2 }, headers[i], false);
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
                    if ((header.Flags & 1) == 0) continue;
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
        var master = (float)Settings.MasterVolume;
        var effects = (float)Settings.EffectsVolume * master;
        var music = (float)Settings.MusicVolume * master * 0.5f;
        lock (_gate)
        {
            for (var v = _voices.Count - 1; v >= 0; v--)
            {
                var voice = _voices[v];
                var gain = voice.Gain * effects;
                for (var i = 0; i < BufferSamples; i++)
                {
                    var index = (int)voice.Position;
                    if (index >= voice.Data.Length - 1) break;
                    var frac = (float)(voice.Position - index);
                    _mix[i] += (voice.Data[index] * (1 - frac) + voice.Data[index + 1] * frac) * gain;
                    voice.Position += voice.Step;
                }
                if (voice.Position >= voice.Data.Length - 1) _voices.RemoveAt(v);
            }
            var track = _fury && _furyMusic.Length > 0 ? _furyMusic : _music;
            if (_musicOn && music > 0 && track.Length > 0)
                for (var i = 0; i < BufferSamples; i++)
                {
                    _mix[i] += track[(int)_musicPosition % track.Length] * music;
                    _musicPosition = (_musicPosition + 1) % track.Length;
                }
        }
        for (var i = 0; i < BufferSamples; i++)
        {
            var s = _mix[i];
            s = s / (1 + Math.Abs(s) * 0.3f);
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

    /// <summary>Procedural sound design.</summary>
    internal static class Synth
    {
        private static float[] Make(double seconds, Func<double, double> sample)
        {
            var data = new float[(int)(seconds * Rate)];
            for (var i = 0; i < data.Length; i++) data[i] = (float)sample(i / (double)Rate);
            return data;
        }

        /// <summary>Leather slap + deep thud; <paramref name="weight"/> makes it bigger and lower.</summary>
        public static float[] Punch(double weight, int seed)
        {
            var random = new Random(seed);
            double low = 0, band = 0;
            return Make(0.22 + weight * 0.08, t =>
            {
                var noise = random.NextDouble() * 2 - 1;
                low += (noise - low) * 0.25;
                band += (low - band) * 0.5;
                var slap = (low - band) * Math.Exp(-t * 70) * 1.4;
                var thudFreq = 95 / weight - t * 120;
                var thud = Math.Sin(2 * Math.PI * Math.Max(35, thudFreq) * t) * Math.Exp(-t * (16 / weight)) * 0.9 * weight;
                var body = low * Math.Exp(-t * 22) * 0.4;
                return (slap + thud + body) * 0.8;
            });
        }

        public static float[] Crit()
        {
            var heavy = Punch(1.6, 23);
            var ring = Make(0.6, t => (Math.Sin(2 * Math.PI * 1567.98 * t) * 0.25 + Math.Sin(2 * Math.PI * 2349.3 * t) * 0.15) * Math.Exp(-t * 6));
            var result = new float[Math.Max(heavy.Length, ring.Length)];
            for (var i = 0; i < result.Length; i++) result[i] = (i < heavy.Length ? heavy[i] : 0) + (i < ring.Length ? ring[i] : 0);
            return result;
        }

        public static float[] FuryStart() => Make(1.1, t =>
        {
            var f = 110 * Math.Pow(4, t / 1.1);
            var saw = 2 * (f * t % 1) - 1;
            return (saw * 0.25 + Math.Sin(2 * Math.PI * f * 1.5 * t) * 0.2) * Math.Min(1, t * 4) * Math.Exp(-Math.Max(0, t - 0.8) * 8);
        });

        public static float[] Coin() => Make(0.18, t => Math.Sin(2 * Math.PI * (t < 0.06 ? 987.77 : 1318.5) * t) * Math.Exp(-t * 18) * 0.45);

        public static float[] Arpeggio(double[] notes, double step) => Make(step * notes.Length + 0.35, t =>
        {
            var index = Math.Min(notes.Length - 1, (int)(t / step));
            var local = t - index * step;
            return (Math.Sin(2 * Math.PI * notes[index] * t) * 0.35 + Math.Sin(4 * Math.PI * notes[index] * t) * 0.08) * Math.Exp(-local * (index == notes.Length - 1 ? 4 : 10));
        });

        public static float[] Sparkle()
        {
            var random = new Random(4);
            return Make(0.5, t => Math.Sin(2 * Math.PI * (1800 + 600 * Math.Sin(t * 40)) * t) * Math.Exp(-t * 7) * 0.2 * (0.6 + 0.4 * random.NextDouble()));
        }

        public static float[] Whoosh()
        {
            var random = new Random(8);
            double filtered = 0;
            return Make(0.4, t =>
            {
                var cutoff = 0.05 + 0.3 * Math.Sin(Math.PI * t / 0.4);
                filtered += ((random.NextDouble() * 2 - 1) - filtered) * cutoff;
                return filtered * Math.Sin(Math.PI * t / 0.4) * 0.6;
            });
        }

        public static float[] Swell() => Make(2.2, t =>
        {
            double[] chord = { 261.63, 329.63, 392.0, 523.25 };
            var s = chord.Sum(f => Math.Sin(2 * Math.PI * f * t)) / chord.Length;
            return s * 0.45 * Math.Min(1, t / 0.8) * Math.Exp(-Math.Max(0, t - 1.4) * 3);
        });

        public static float[] Buzz() => Make(0.15, t => Math.Sign(Math.Sin(2 * Math.PI * 140 * t)) * 0.15 * Math.Exp(-t * 12));

        /// <summary>Drum-and-bass loop per studio: tempo and key rise with progress; fury doubles the drums.</summary>
        public static float[] Music(int environment, bool fury)
        {
            var bpm = 92 + environment * 7 + (fury ? 24 : 0);
            var beat = 60.0 / bpm;
            var length = beat * 16;
            var root = 55 * Math.Pow(2, (environment % 5) / 12.0);
            int[] bassLine = { 0, 0, 7, 5, 0, 0, 3, 5 };
            int[] lead = environment >= 5 ? new[] { 12, 15, 19, 15, 12, 10, 7, 10 } : new[] { 7, 10, 12, 10, 7, 5, 3, 5 };
            var random = new Random(environment);
            var noise = Enumerable.Range(0, 4096).Select(_ => random.NextDouble() * 2 - 1).ToArray();
            return Make(length, t =>
            {
                var beatIndex = (int)(t / beat);
                var inBeat = t - beatIndex * beat;
                var eighth = (int)(t / (beat / 2));
                var inEighth = t - eighth * beat / 2;
                var kick = (beatIndex % 2 == 0 || fury) ? Math.Sin(2 * Math.PI * (60 - inBeat * 80) * inBeat) * Math.Exp(-inBeat * 18) * 0.5 : 0;
                var snare = beatIndex % 2 == 1 ? noise[(int)(t * Rate) % noise.Length] * Math.Exp(-inBeat * 25) * 0.25 : 0;
                var hat = noise[(int)(t * Rate * 3) % noise.Length] * Math.Exp(-inEighth * 60) * 0.06;
                var bassNote = root * Math.Pow(2, bassLine[eighth / 2 % bassLine.Length] / 12.0);
                var bass = Math.Sign(Math.Sin(2 * Math.PI * bassNote * t)) * 0.12 * Math.Exp(-inEighth * 6);
                var leadNote = root * 4 * Math.Pow(2, lead[eighth % lead.Length] / 12.0);
                var leadOn = environment >= 2 && eighth % 2 == 0;
                var melody = leadOn ? (2 * (leadNote * t % 1) - 1) * 0.05 * Math.Exp(-inEighth * 8) : 0;
                return kick + snare + hat + bass + melody;
            });
        }
    }
}

/// <summary>Xbox-compatible controller through XInput: buttons and vibration (if a controller is connected).</summary>
public sealed class Gamepad
{
    private ushort _previous;
    private DateTime _vibrationEnd;
    private bool _vibrating;

    public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000, LeftShoulder = 0x0100, RightShoulder = 0x0200, Start = 0x0010;

    public bool Connected { get; private set; }

    /// <summary>Buttons pressed since the last poll.</summary>
    public ushort Poll()
    {
        try
        {
            if (XInputGetState(0, out var state) != 0)
            {
                Connected = false;
                return 0;
            }
            Connected = true;
            var pressed = (ushort)(state.Gamepad.Buttons & ~_previous);
            _previous = state.Gamepad.Buttons;
            if (_vibrating && DateTime.UtcNow > _vibrationEnd) SetVibration(0, 0);
            return pressed;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            Connected = false;
            return 0;
        }
    }

    public void Vibrate(double strength, TimeSpan duration)
    {
        if (!Connected) return;
        var value = (ushort)(Math.Clamp(strength, 0, 1) * 65535);
        SetVibration(value, (ushort)(value / 2));
        _vibrationEnd = DateTime.UtcNow + duration;
    }

    public void Stop()
    {
        if (_vibrating) SetVibration(0, 0);
    }

    private void SetVibration(ushort left, ushort right)
    {
        try
        {
            var vibration = new XINPUT_VIBRATION { LeftMotorSpeed = left, RightMotorSpeed = right };
            XInputSetState(0, ref vibration);
            _vibrating = left > 0 || right > 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            Connected = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint PacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_VIBRATION
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    [DllImport("xinput1_4.dll")] private static extern int XInputGetState(int index, out XINPUT_STATE state);
    [DllImport("xinput1_4.dll")] private static extern int XInputSetState(int index, ref XINPUT_VIBRATION vibration);
}
