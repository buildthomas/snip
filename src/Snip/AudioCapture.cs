using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Snip;

/// Places captured packets on the recording's timeline by their timestamps, so sources that pause (system sound
/// sends nothing while silent) or drift stay in sync with the video. Sources are summed; anything missing is silence.
internal sealed class AudioMixer
{
    public const int Rate = 48000, Channels = 2;
    private const int WindowFrames = Rate * 4;
    private readonly float[] _mix = new float[WindowFrames * Channels];

    public long Written { get; private set; }

    /// Adds interleaved stereo samples starting at the given frame on the timeline; late or far-future parts are dropped.
    public void Add(ReadOnlySpan<float> samples, long firstFrame)
    {
        var frames = samples.Length / Channels;
        for (int i = 0; i < frames; i++)
        {
            var at = firstFrame + i - Written;
            if (at < 0) continue;
            if (at >= WindowFrames) break;
            _mix[at * Channels] += samples[i * Channels];
            _mix[at * Channels + 1] += samples[i * Channels + 1];
        }
    }

    /// Takes the mixed audio up to (not including) the given frame as 16-bit PCM.
    public short[] TakeUntil(long frame)
    {
        var frames = (int)Math.Clamp(frame - Written, 0, WindowFrames);
        var pcm = new short[frames * Channels];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Round(Math.Clamp(_mix[i], -1f, 1f) * 32767f);
        Array.Copy(_mix, frames * Channels, _mix, 0, _mix.Length - frames * Channels);
        Array.Clear(_mix, _mix.Length - frames * Channels, frames * Channels);
        Written += frames;
        return pcm;
    }
}

/// Packets follow on from the previous one (the burst after silence shares one stamp and would otherwise stack);
/// the stamp only wins for a real gap, or for large drift, where the part that would overlap is dropped.
internal static class AudioTimeline
{
    public const long GapFrames = AudioMixer.Rate * 30 / 1000;
    public const long DriftFrames = AudioMixer.Rate / 5;

    public static (long At, int Skip) Place(long nextFrame, long stampedFrame, int frames)
    {
        if (nextFrame < 0 || stampedFrame > nextFrame + GapFrames) return (stampedFrame, 0);
        if (stampedFrame < nextFrame - DriftFrames) return (nextFrame, (int)Math.Min(frames, nextFrame - stampedFrame));
        return (nextFrame, 0);
    }
}

/// System sound (WASAPI loopback of the default output) and/or the default microphone, as 48 kHz stereo.
internal sealed unsafe class AudioCapture
{
    private const long LatencyFrames = AudioMixer.Rate / 10;

    private sealed class Source
    {
        public required string Name;
        public IntPtr Client, Capture;
        public long NextFrame = -1;
        public bool Dead;
        public long Packets, Frames, SilentPackets, FirstAt = long.MinValue;
    }

    private readonly List<Source> _sources = new();
    private readonly AudioMixer _mixer = new();
    private Action<short[], long>? _sink;
    private Thread? _thread;
    private volatile bool _stop;
    private long _startTimestamp;

    public string? Error { get; private set; }

    public static AudioCapture Create(bool systemSound, bool microphone)
    {
        var a = new AudioCapture();
        try
        {
            if (systemSound) a.Open("system sound", 0 /* eRender */, loopback: true);
            if (microphone) a.Open("microphone", 1 /* eCapture */, loopback: false);
        }
        catch
        {
            a.Release();
            throw;
        }
        return a;
    }

    private void Open(string name, int flow, bool loopback)
    {
        IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero;
        var s = new Source { Name = name };
        try
        {
            var clsid = CLSID_MMDeviceEnumerator;
            var iid = IID_IMMDeviceEnumerator;
            Check(CoCreateInstance(&clsid, IntPtr.Zero, 0x17, &iid, out enumerator), name);
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)Slot(enumerator, 4))(enumerator, flow, 0, &device);
            if (hr == E_NOTFOUND) throw new InvalidOperationException(flow == 1 ? "No microphone found." : "No sound output device found.");
            Check(hr, name);
            var iidClient = IID_IAudioClient;
            IntPtr client;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)Slot(device, 3))(device, &iidClient, 0x17, IntPtr.Zero, &client);
            AccessCheck(hr, flow);
            s.Client = client;

            var fmt = new WaveFormat
            {
                Tag = 3 /* IEEE_FLOAT */, Channels = AudioMixer.Channels, SamplesPerSec = AudioMixer.Rate,
                AvgBytesPerSec = AudioMixer.Rate * 8, BlockAlign = 8, BitsPerSample = 32,
            };
            uint flags = 0x80000000 /* AUTOCONVERTPCM */ | 0x08000000 /* SRC_DEFAULT_QUALITY */ | (loopback ? 0x00020000u /* LOOPBACK */ : 0);
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, WaveFormat*, Guid*, int>)Slot(client, 3))(
                client, 0 /* shared */, flags, 2_000_000 /* 200 ms */, 0, &fmt, null);
            AccessCheck(hr, flow);
            var iidCapture = IID_IAudioCaptureClient;
            IntPtr capture;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(client, 14))(client, &iidCapture, &capture), name);
            s.Capture = capture;
            _sources.Add(s);
        }
        catch
        {
            Rel(ref s.Capture);
            Rel(ref s.Client);
            throw;
        }
        finally
        {
            Rel(ref device);
            Rel(ref enumerator);
        }
    }

    /// Starts capturing; the sink receives 16-bit stereo PCM and the frame index of its first sample.
    public void Start(long startTimestamp, Action<short[], long> sink)
    {
        _startTimestamp = startTimestamp;
        _sink = sink;
        foreach (var s in _sources) Check(Call(s.Client, 10), s.Name);
        _thread = new Thread(Run) { IsBackground = true, Name = "Snip audio" };
        _thread.Start();
    }

    /// Stops and hands over everything up to the given frame (the end of the video).
    public void Stop(long endFrame)
    {
        _stop = true;
        _thread?.Join();
        try
        {
            Drain();
            var first = _mixer.Written;
            var pcm = _mixer.TakeUntil(endFrame);
            if (pcm.Length > 0) _sink?.Invoke(pcm, first);
        }
        catch (Exception ex) { Error ??= ex.Message; }
        foreach (var s in _sources)
        {
            if (s.Client != IntPtr.Zero) Call(s.Client, 11);
            Log.Trace($"audio {s.Name}: {s.Packets} packets ({s.SilentPackets} silent), {s.Frames} frames, first at frame {s.FirstAt}, mixer at {_mixer.Written}, end {endFrame}");
        }
    }

    public void Release()
    {
        foreach (var s in _sources)
        {
            Rel(ref s.Capture);
            Rel(ref s.Client);
        }
        _sources.Clear();
    }

    private void Run()
    {
        CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            while (!_stop)
            {
                Thread.Sleep(10);
                Drain();
                var until = NowFrame() - LatencyFrames;
                if (until > _mixer.Written)
                {
                    var first = _mixer.Written;
                    _sink?.Invoke(_mixer.TakeUntil(until), first);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"audio failed: {ex}");
            Error ??= ex.Message;
        }
        finally
        {
            CoUninitialize();
        }
    }

    private long NowFrame() => (Stopwatch.GetTimestamp() - _startTimestamp) * AudioMixer.Rate / Stopwatch.Frequency;

    private void Drain()
    {
        foreach (var s in _sources)
        {
            if (s.Dead) continue;
            while (true)
            {
                uint packet;
                var hr = Call(s.Capture, 5, &packet);
                if (hr < 0)
                {
                    s.Dead = true;
                    Log.Write($"audio: {s.Name} stopped (0x{hr:X8})");
                    Error ??= $"The {s.Name} went away during recording.";
                    break;
                }
                if (packet == 0) break;
                float* data;
                uint frames, flags;
                ulong devicePos, qpc;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, float**, uint*, uint*, ulong*, ulong*, int>)Slot(s.Capture, 3))(
                    s.Capture, &data, &frames, &flags, &devicePos, &qpc), s.Name);
                var raw = (flags & 0x4 /* TIMESTAMP_ERROR */) != 0 && s.NextFrame >= 0
                    ? s.NextFrame : FrameAt(qpc, _startTimestamp, Stopwatch.Frequency);
                var (first, skip) = AudioTimeline.Place(s.NextFrame, raw, (int)frames);
                if (first != s.NextFrame || skip > 0)
                    Log.Trace($"audio {s.Name}: packet of {frames} at frame {first}, " +
                        $"{(s.NextFrame < 0 ? "first" : $"stamped {raw - s.NextFrame} from the previous end, {skip} dropped")}");
                s.NextFrame = first + frames - skip;
                s.Packets++; s.Frames += frames;
                if ((flags & 0x2) != 0) s.SilentPackets++;
                if (s.FirstAt == long.MinValue) s.FirstAt = first;
                if ((flags & 0x2 /* SILENT */) == 0 && frames > skip)
                    _mixer.Add(new ReadOnlySpan<float>(data + skip * AudioMixer.Channels, (int)(frames - skip) * AudioMixer.Channels), first);
                Call(s.Capture, 4, frames);
            }
        }
    }

    /// Timeline frame of a packet stamped in 100 ns QPC units; the start is a raw QPC tick count. Converted without
    /// multiplying the raw counter, which overflows after a day or two of uptime.
    public static long FrameAt(ulong qpc100ns, long startTicks, long ticksPerSecond)
    {
        var start100ns = startTicks / ticksPerSecond * 10_000_000 + startTicks % ticksPerSecond * 10_000_000 / ticksPerSecond;
        return ((long)qpc100ns - start100ns) * AudioMixer.Rate / 10_000_000;
    }

    private static void AccessCheck(int hr, int flow)
    {
        if (hr == E_ACCESSDENIED && flow == 1)
            throw new InvalidOperationException("Windows blocked microphone access. Turn on \"Let desktop apps access your microphone\" in Settings > Privacy & security > Microphone.");
        Check(hr, flow == 1 ? "microphone" : "system sound");
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"Couldn't record the {what} (0x{hr:X8}).", hr);
    }

    private static void* Slot(IntPtr p, int i) => (*(void***)p)[i];
    private static int Call(IntPtr p, int i) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(p, i))(p);
    private static int Call(IntPtr p, int i, uint a) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(p, i))(p, a);
    private static int Call(IntPtr p, int i, uint* a) => ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(p, i))(p, a);

    private static void Rel(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        Call(p, 2);
        p = IntPtr.Zero;
    }

    private const int E_NOTFOUND = unchecked((int)0x80070490);
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private static readonly Guid CLSID_MMDeviceEnumerator = new("bcde0395-e52f-467c-8e3d-c4579291692e");
    private static readonly Guid IID_IMMDeviceEnumerator = new("a95664d2-9614-4f35-a746-de8db63617e6");
    private static readonly Guid IID_IAudioClient = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
    private static readonly Guid IID_IAudioCaptureClient = new("c8adbd64-e71e-48a0-a4de-185c395cd317");

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, out IntPtr obj);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
