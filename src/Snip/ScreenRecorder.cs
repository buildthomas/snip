using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Snip;

internal sealed record RecordingResult(string Path, TimeSpan Duration, int Frames, long Bytes, Bitmap? LastFrame, string? StopReason);

internal static class RecordingMath
{
    public const int Fps = 30;
    public const int MaxWidth = 4096, MaxHeight = 4096, MaxPixels = 4096 * 2304;
    public const int MinSide = 16;
    public const long KeepAliveMs = 1000;

    // H.264 needs even dimensions; drop the odd row or column.
    public static Rectangle Even(Rectangle r) => new(r.X, r.Y, r.Width & ~1, r.Height & ~1);

    public static string? SizeProblem(Rectangle r)
    {
        if (r.Width < MinSide || r.Height < MinSide) return "The selection is too small to record.";
        if (r.Width > MaxWidth || r.Height > MaxHeight || (long)r.Width * r.Height > MaxPixels)
            return $"Recording is limited to about 4K ({MaxWidth}×2304). Select a smaller area.";
        return null;
    }

    // Upper bound for the encoder; screen content usually lands well below it.
    public static uint Bitrate(int width, int height) => (uint)Math.Clamp((long)width * height * Fps / 16, 500_000, 20_000_000);

    public static bool ShouldWrite(long nowMs, long dueMs, bool dirty, long lastWriteMs) =>
        nowMs >= dueMs && (dirty || nowMs - lastWriteMs >= KeepAliveMs);
}

/// Records a screen rectangle to H.264 MP4: Desktop Duplication into a GPU texture, Media Foundation's (hardware) encoder.
internal sealed unsafe class ScreenRecorder
{
    private readonly Rectangle _rect;
    private readonly string _path;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _go = new();
    private readonly Stopwatch _clock = new();
    private volatile bool _stop, _cancelled;
    private long _notBefore;
    private readonly object _stateGate = new();
    private bool _started;
    private string? _failure;
    private Action? _onStarted;
    private Action<string>? _onFailed;
    private string? _stopReason;
    private int _frames;
    private Bitmap? _lastFrame;
    private long _lastTimestamp;

    public event Action<string>? AutoStopped;
    public TimeSpan Elapsed => _clock.Elapsed;
    public Rectangle Rect => _rect;
    public string OutputPath => _path;

    private readonly bool _systemSound, _microphone;
    private readonly object _writerGate = new();
    private AudioCapture? _audio;
    private uint _audioStream;
    private long _startTimestamp;
    internal Action<short[], long>? AudioTap;

    private ScreenRecorder(Rectangle rect, string path, bool systemSound, bool microphone)
    {
        _rect = rect;
        _path = path;
        _systemSound = systemSound;
        _microphone = microphone;
        _thread = new Thread(Run) { IsBackground = true, Name = "Snip recorder" };
    }

    /// Sets up device, duplication and encoder in the background without capturing; Begin starts, Cancel discards.
    public static ScreenRecorder Prepare(Rectangle screenRect, string path, bool systemSound, bool microphone)
    {
        var r = new ScreenRecorder(RecordingMath.Even(screenRect), path, systemSound, microphone);
        r._thread.Start();
        return r;
    }

    public bool Matches(Rectangle screenRect, bool systemSound, bool microphone)
    {
        lock (_stateGate)
            return !_cancelled && _failure == null && _rect == RecordingMath.Even(screenRect)
                && systemSound == _systemSound && microphone == _microphone;
    }

    /// Starts capturing; the first frame is taken no earlier than the given delay, so a closing overlay is gone.
    public void Begin(int notBeforeMs = 60)
    {
        _notBefore = Stopwatch.GetTimestamp() + notBeforeMs * Stopwatch.Frequency / 1000;
        _go.Set();
    }

    public void Cancel()
    {
        _cancelled = true;
        _go.Set();
    }

    /// Runs the action once capture is running (right away if it already is), on the recorder thread or the caller's.
    public void WhenStarted(Action action)
    {
        lock (_stateGate)
        {
            if (!_started) { _onStarted += action; return; }
        }
        action();
    }

    /// Runs the action if preparing or starting fails (right away if it already did).
    public void WhenFailed(Action<string> action)
    {
        string? failure;
        lock (_stateGate)
        {
            failure = _failure;
            if (failure == null) { _onFailed += action; return; }
        }
        action(failure);
    }

    /// Prepares, begins and waits until capture runs; for tests and tools.
    public static ScreenRecorder Start(Rectangle screenRect, string path, bool systemSound = false, bool microphone = false)
    {
        var r = Prepare(screenRect, path, systemSound, microphone);
        using var done = new ManualResetEventSlim();
        string? failure = null;
        r.WhenStarted(done.Set);
        r.WhenFailed(f => { failure = f; done.Set(); });
        r.Begin(0);
        done.Wait();
        if (failure != null) throw new InvalidOperationException(failure);
        return r;
    }

    private void MarkStarted()
    {
        Action? a;
        lock (_stateGate) { _started = true; a = _onStarted; _onStarted = null; }
        Notify(a);
    }

    private void MarkFailed(string message)
    {
        Action<string>? a;
        lock (_stateGate) { _failure = message; a = _onFailed; _onFailed = null; }
        if (a != null) Notify(() => a(message));
    }

    // Callbacks run on the recorder thread; one that throws must not take the process down.
    private static void Notify(Action? a)
    {
        try { a?.Invoke(); }
        catch (Exception ex) { Log.Write($"recording callback failed: {ex}"); }
    }

    public RecordingResult Stop()
    {
        _stop = true;
        _go.Set();
        _thread.Join();
        var bytes = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        if (bytes == 0) throw new IOException(_stopReason ?? "The recording produced no video.");
        return new RecordingResult(_path, TimeSpan.FromTicks(_lastTimestamp), _frames, bytes, _lastFrame, _stopReason);
    }

    // ---------- Recording thread ----------

    private sealed class Source
    {
        public IntPtr Output1, Dup;
        public Rectangle Desktop, Part;
    }

    private readonly List<Source> _sources = new();
    private IntPtr _device, _context, _staging, _accum, _manager, _writer, _memDc, _dib, _oldBmp;
    private byte* _bits;
    private uint _stream;
    private bool _mfStarted, _gpu;

    private void Run()
    {
        CoInitializeEx(IntPtr.Zero, 0 /* multithreaded */);
        try
        {
            Init();
            _go.Wait();
            if (_cancelled)
            {
                Log.Trace("recording: prepared but not used");
                Abandon();
                return;
            }
            var wait = (_notBefore - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
            if (wait > 0) Thread.Sleep((int)wait);
            // Fresh duplications, so a frame from while the overlay was still up can't sneak in.
            foreach (var s in _sources)
            {
                Release(ref s.Dup);
                if (!DuplicateSoon(s)) throw new InvalidOperationException("Windows refused screen recording right now. Try again in a moment.");
            }
            if (_systemSound || _microphone) _audio = AudioCapture.Create(_systemSound, _microphone);
        }
        catch (Exception ex)
        {
            Log.Write($"recording start failed: {ex}");
            Abandon();
            MarkFailed(ex.Message);
            return;
        }

        try
        {
            Loop();
        }
        catch (Exception ex)
        {
            Log.Write($"recording failed: {ex}");
            _stopReason = $"Recording stopped: {ex.Message}";
            var reason = _stopReason;
            Notify(() => AutoStopped?.Invoke(reason));
        }
        if (_audio != null)
        {
            _audio.Stop(_lastTimestamp * AudioMixer.Rate / 10_000_000);
            if (_audio.Error != null) _stopReason ??= $"Sound: {_audio.Error}";
        }
        try
        {
            if (_writer != IntPtr.Zero && _frames > 0) Check(Call(_writer, 11), "finish the video");
        }
        catch (Exception ex)
        {
            Log.Write($"recording finalize failed: {ex}");
            _stopReason = $"Couldn't finish the video: {ex.Message}";
        }
        Cleanup();
        CoUninitialize();
        if (_frames == 0) DeleteOutput();
    }

    private void Abandon()
    {
        Release(ref _writer);
        Cleanup();
        CoUninitialize();
        DeleteOutput();
    }

    private void DeleteOutput()
    {
        try { File.Delete(_path); }
        catch (Exception ex) { Log.Write($"couldn't delete the unused recording file: {ex.Message}"); }
    }

    private void Loop()
    {
        long intervalMs = 1000 / RecordingMath.Fps;
        long due = 0, lastWrite = long.MinValue / 2, nextDiskCheck = 0;
        var dirty = true;
        var root = Path.GetPathRoot(Path.GetFullPath(_path))!;
        _startTimestamp = Stopwatch.GetTimestamp();
        _clock.Start();
        MarkStarted();
        while (!_stop)
        {
            var now = _clock.ElapsedMilliseconds;
            var timeout = (uint)Math.Clamp(due - now, 0, 100);
            var anyLive = false;
            for (int i = 0; i < _sources.Count; i++)
            {
                var s = _sources[i];
                if (s.Dup == IntPtr.Zero && !TryDuplicate(s)) continue;
                anyLive = true;
                if (Acquire(s, i == 0 ? timeout : 0)) dirty = true;
            }
            if (!anyLive) Thread.Sleep(100);

            now = _clock.ElapsedMilliseconds;
            if (now >= due)
            {
                if (RecordingMath.ShouldWrite(now, due, dirty, lastWrite))
                {
                    WriteFrame(now);
                    if (_frames == 1) _audio?.Start(_startTimestamp, WriteAudio);
                    dirty = false;
                    lastWrite = now;
                }
                due = now + intervalMs;
            }
            if (now >= nextDiskCheck)
            {
                nextDiskCheck = now + 2000;
                if (FreeBytes(root) < 1L << 30)
                {
                    _stopReason = "Recording stopped: less than 1 GB of disk space left.";
                    var reason = _stopReason;
                    Notify(() => AutoStopped?.Invoke(reason));
                    break;
                }
            }
        }
        var end = _clock.ElapsedMilliseconds;
        if (end > lastWrite) WriteFrame(end);
    }

    private static long FreeBytes(string root)
    {
        try { return new DriveInfo(root).AvailableFreeSpace; }
        catch (Exception) { return long.MaxValue; }
    }

    // Returns true when the desktop image or the pointer changed.
    private bool Acquire(Source s, uint timeoutMs)
    {
        FrameInfo info;
        IntPtr resource;
        var hr = Call(s.Dup, 8, timeoutMs, &info, &resource);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return false;
        if (hr == DXGI_ERROR_ACCESS_LOST)
        {
            Release(ref s.Dup);
            return true;
        }
        Check(hr, "read the screen");
        try
        {
            if (info.LastPresentTime != 0 && resource != IntPtr.Zero)
            {
                var iid = IID_ID3D11Texture2D;
                IntPtr tex;
                Check(Call(resource, 0, &iid, &tex), "read the screen");
                var box = new Box
                {
                    Left = (uint)(s.Part.Left - s.Desktop.Left), Top = (uint)(s.Part.Top - s.Desktop.Top), Front = 0,
                    Right = (uint)(s.Part.Right - s.Desktop.Left), Bottom = (uint)(s.Part.Bottom - s.Desktop.Top), Back = 1,
                };
                CallVoid(_context, 46, _gpu ? _accum : _staging, 0u, (uint)(s.Part.X - _rect.X), (uint)(s.Part.Y - _rect.Y), 0u, tex, 0u, &box);
                Release(ref tex);
            }
            return info.LastPresentTime != 0 || info.LastMouseUpdateTime != 0;
        }
        finally
        {
            if (resource != IntPtr.Zero) Release(ref resource);
            Call(s.Dup, 14);
        }
    }

    private void WriteFrame(long nowMs)
    {
        if (!_gpu) { WriteFrameCpu(nowMs); return; }
        try
        {
            WriteFrameGpu(nowMs);
        }
        catch (Exception ex) when (_frames == 0)
        {
            Log.Write($"recording: GPU path failed on the first frame, using the CPU path: {ex.Message}");
            SwitchToCpu();
            WriteFrameCpu(nowMs);
        }
    }

    private void WriteFrameGpu(long nowMs)
    {
        IntPtr tex = IntPtr.Zero, surface = IntPtr.Zero, buffer = IntPtr.Zero, buffer2d = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            var td = Desc(usage: 0, bind: 0x20 /* RENDER_TARGET */, cpu: 0, misc: 0x200 /* GDI_COMPATIBLE */);
            Check(Call(_device, 5, &td, IntPtr.Zero, &tex), "encode");
            CallVoid(_context, 47, tex, _accum);

            var iidSurface = IID_IDXGISurface1;
            Check(Call(tex, 0, &iidSurface, &surface), "encode");
            IntPtr dc;
            if (Call(surface, 11, 0, &dc) >= 0)
            {
                DrawCursor(dc);
                ((delegate* unmanaged[Stdcall]<IntPtr, void*, int>)Slot(surface, 12))(surface, null);
            }

            var iidTex = IID_ID3D11Texture2D;
            Check(MFCreateDXGISurfaceBuffer(&iidTex, tex, 0, 0, out buffer), "encode");
            var iid2d = IID_IMF2DBuffer;
            uint len;
            Check(Call(buffer, 0, &iid2d, &buffer2d), "encode");
            Check(Call(buffer2d, 7, (void*)&len), "encode");
            Check(Call(buffer, 6, len), "encode");
            Check(MFCreateSample(out sample), "encode");
            Check(Call(sample, 42, buffer), "encode");
            Submit(sample, nowMs);
        }
        finally
        {
            Release(ref sample);
            Release(ref buffer2d);
            Release(ref buffer);
            Release(ref surface);
            Release(ref tex);
        }
    }

    private void SwitchToCpu()
    {
        Release(ref _writer);
        DeleteOutput();
        _gpu = false;
        CallVoid(_context, 47, _staging, _accum);
        InitEncoder();
    }

    private void Submit(IntPtr sample, long nowMs)
    {
        var t = nowMs * 10_000;
        Check(Call(sample, 36, t), "encode");
        Check(Call(sample, 38, 10_000_000L / RecordingMath.Fps), "encode");
        lock (_writerGate) Check(Call(_writer, 6, _stream, sample), "encode");
        _frames++;
        _lastTimestamp = t + 10_000_000L / RecordingMath.Fps;
    }

    private void WriteAudio(short[] pcm, long firstFrame)
    {
        if (pcm.Length == 0) return;
        AudioTap?.Invoke(pcm, firstFrame);
        var len = pcm.Length * 2;
        IntPtr buffer = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            Check(MFCreateMemoryBuffer((uint)len, out buffer), "encode sound");
            byte* dst;
            Check(Call(buffer, 3, &dst, (uint*)null, (uint*)null), "encode sound");
            fixed (short* src = pcm) Buffer.MemoryCopy(src, dst, len, len);
            Call(buffer, 4);
            Check(Call(buffer, 6, (uint)len), "encode sound");
            Check(MFCreateSample(out sample), "encode sound");
            Check(Call(sample, 42, buffer), "encode sound");
            Check(Call(sample, 36, firstFrame * 10_000_000 / AudioMixer.Rate), "encode sound");
            Check(Call(sample, 38, (long)(pcm.Length / AudioMixer.Channels) * 10_000_000 / AudioMixer.Rate), "encode sound");
            lock (_writerGate) Check(Call(_writer, 6, _audioStream, sample), "encode sound");
        }
        finally
        {
            Release(ref sample);
            Release(ref buffer);
        }
    }

    private void WriteFrameCpu(long nowMs)
    {
        EnsureDib();
        int w = _rect.Width, h = _rect.Height, stride = w * 4, len = stride * h;
        Mapped mapped;
        Check(Call(_context, 14, _staging, 0u, 1u /* D3D11_MAP_READ */, 0u, &mapped), "read the screen");
        for (int y = 0; y < h; y++)
            Buffer.MemoryCopy((byte*)mapped.Data + (long)y * mapped.RowPitch, _bits + (long)y * stride, stride, stride);
        CallVoid(_context, 15, _staging, 0u);
        DrawCursor(_memDc);

        IntPtr buffer = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            Check(MFCreateMemoryBuffer((uint)len, out buffer), "encode");
            byte* dst;
            Check(Call(buffer, 3, &dst, (uint*)null, (uint*)null), "encode");
            Buffer.MemoryCopy(_bits, dst, len, len);
            Call(buffer, 4);
            Check(Call(buffer, 6, (uint)len), "encode");
            Check(MFCreateSample(out sample), "encode");
            Check(Call(sample, 42, buffer), "encode");
            Submit(sample, nowMs);
        }
        finally
        {
            Release(ref sample);
            Release(ref buffer);
        }
    }

    private IntPtr _cursorHandle;
    private Point _cursorHotspot;

    private void DrawCursor(IntPtr dc)
    {
        var ci = new CursorInfo { Size = sizeof(CursorInfo) };
        if (!GetCursorInfo(ref ci) || (ci.Flags & 1) == 0 || ci.Cursor == IntPtr.Zero) return;
        if (ci.Cursor != _cursorHandle)
        {
            _cursorHandle = ci.Cursor;
            _cursorHotspot = Point.Empty;
            if (GetIconInfo(ci.Cursor, out var ii))
            {
                _cursorHotspot = new Point(ii.XHotspot, ii.YHotspot);
                if (ii.Mask != IntPtr.Zero) DeleteObject(ii.Mask);
                if (ii.Color != IntPtr.Zero) DeleteObject(ii.Color);
            }
        }
        DrawIconEx(dc, ci.X - _cursorHotspot.X - _rect.X, ci.Y - _cursorHotspot.Y - _rect.Y, ci.Cursor, 0, 0, 0, IntPtr.Zero, 3);
    }

    // ---------- Setup ----------

    private void Init()
    {
        var problem = RecordingMath.SizeProblem(_rect);
        if (problem != null) throw new InvalidOperationException(problem);
        var sw = Stopwatch.StartNew();

        var iidFactory = IID_IDXGIFactory1;
        Check(CreateDXGIFactory1(&iidFactory, out var factory), "set up screen recording");
        IntPtr adapter = IntPtr.Zero;
        try
        {
            for (uint a = 0; ; a++)
            {
                IntPtr ad;
                if (Call(factory, 7, a, &ad) != 0) break;
                var found = false;
                for (uint o = 0; ; o++)
                {
                    IntPtr output;
                    if (Call(ad, 7, o, &output) != 0) break;
                    OutputDesc desc;
                    Call(output, 7, &desc);
                    var d = Rectangle.FromLTRB(desc.Left, desc.Top, desc.Right, desc.Bottom);
                    var part = Rectangle.Intersect(d, _rect);
                    if (desc.Attached == 0 || part.Width <= 0 || part.Height <= 0) { Release(ref output); continue; }
                    if (desc.Rotation > 1)
                    {
                        Release(ref output);
                        throw new InvalidOperationException("Recording a rotated display isn't supported yet.");
                    }
                    var iid1 = IID_IDXGIOutput1;
                    IntPtr output1;
                    Check(Call(output, 0, &iid1, &output1), "set up screen recording");
                    Release(ref output);
                    _sources.Add(new Source { Output1 = output1, Desktop = d, Part = part });
                    found = true;
                }
                if (found)
                {
                    if (adapter != IntPtr.Zero)
                    {
                        Release(ref ad);
                        throw new InvalidOperationException("The selection spans displays on different graphics adapters. Record one display at a time.");
                    }
                    adapter = ad;
                }
                else Release(ref ad);
            }
        }
        finally
        {
            Release(ref factory);
        }
        if (adapter == IntPtr.Zero) throw new InvalidOperationException("No display found for the selection.");

        try
        {
            _gpu = D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20 | 0x800 /* BGRA, VIDEO_SUPPORT */, null, 0, 7, out _device, out _, out _context) >= 0;
            if (!_gpu)
                Check(D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, null, 0, 7, out _device, out _, out _context), "set up screen recording");
        }
        finally
        {
            Release(ref adapter);
        }
        if (_gpu) _gpu = TrySetUpGpuEncoding();
        Log.Trace($"recording setup: device {sw.ElapsedMilliseconds} ms");
        foreach (var s in _sources)
            if (!DuplicateSoon(s)) throw new InvalidOperationException("Windows refused screen recording right now. Try again in a moment.");

        var td = Desc(usage: 3 /* STAGING */, bind: 0, cpu: 0x20000 /* READ */, misc: 0);
        IntPtr staging;
        Check(Call(_device, 5, &td, IntPtr.Zero, &staging), "set up screen recording");
        _staging = staging;
        if (_gpu)
        {
            var ad = Desc(usage: 0, bind: 0x20, cpu: 0, misc: 0);
            IntPtr accum;
            Check(Call(_device, 5, &ad, IntPtr.Zero, &accum), "set up screen recording");
            _accum = accum;
        }

        Log.Trace($"recording setup: duplication {sw.ElapsedMilliseconds} ms");
        Check(MFStartup(0x0002_0070, 1 /* LITE: no sockets */), "start the video encoder");
        _mfStarted = true;
        Log.Trace($"recording setup: MF startup {sw.ElapsedMilliseconds} ms");
        try
        {
            InitEncoder();
        }
        catch (Exception ex) when (_gpu)
        {
            Log.Write($"recording: GPU encoder setup failed, using the CPU path: {ex.Message}");
            Release(ref _writer);
            DeleteOutput();
            _gpu = false;
            InitEncoder();
        }
        Log.Trace($"recording setup: encoder {sw.ElapsedMilliseconds} ms");
        Log.Write($"recording prepared: {_rect.Width}x{_rect.Height} at {_rect.X},{_rect.Y}, {_sources.Count} display(s), " +
            $"{(_gpu ? "GPU" : "CPU")} path, system sound {(_systemSound ? "on" : "off")}, microphone {(_microphone ? "on" : "off")}");
    }

    private TextureDesc Desc(uint usage, uint bind, uint cpu, uint misc) => new()
    {
        Width = (uint)_rect.Width, Height = (uint)_rect.Height, MipLevels = 1, ArraySize = 1, Format = 87 /* B8G8R8A8 */,
        SampleCount = 1, Usage = usage, BindFlags = bind, CpuAccess = cpu, MiscFlags = misc,
    };

    private bool TrySetUpGpuEncoding()
    {
        IntPtr mt = IntPtr.Zero;
        try
        {
            var iid = IID_ID3D10Multithread;
            if (Call(_device, 0, &iid, &mt) < 0) return false;
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(mt, 5))(mt, 1);
            if (MFCreateDXGIDeviceManager(out var token, out _manager) < 0) return false;
            return Call(_manager, 7, _device, token) >= 0;
        }
        finally
        {
            Release(ref mt);
        }
    }

    private void EnsureDib()
    {
        if (_dib != IntPtr.Zero) return;
        var bmi = new BitmapInfoHeader { Size = 40, Width = _rect.Width, Height = -_rect.Height, Planes = 1, BitCount = 32 };
        _memDc = CreateCompatibleDC(IntPtr.Zero);
        _dib = CreateDIBSection(_memDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new OutOfMemoryException("Not enough memory to record this area.");
        _bits = (byte*)bits;
        _oldBmp = SelectObject(_memDc, _dib);
    }

    // A recorder that was just cancelled can still hold the display's duplication for a moment.
    private bool DuplicateSoon(Source s)
    {
        for (var sw = Stopwatch.StartNew(); !_cancelled; Thread.Sleep(50))
            if (TryDuplicate(s) || sw.ElapsedMilliseconds > 2000) return s.Dup != IntPtr.Zero;
        return false;
    }

    private bool TryDuplicate(Source s)
    {
        IntPtr dup;
        if (Call(s.Output1, 22, _device, &dup) != 0) return false;
        s.Dup = dup;
        return true;
    }

    private void InitEncoder()
    {
        int w = _rect.Width, h = _rect.Height;

        IntPtr attrs = IntPtr.Zero, outType = IntPtr.Zero, inType = IntPtr.Zero, enc = IntPtr.Zero;
        try
        {
            Check(MFCreateAttributes(out attrs, 2), "start the video encoder");
            SetU32(attrs, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            SetU32(attrs, MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            if (_gpu)
            {
                var key = MF_SINK_WRITER_D3D_MANAGER;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, int>)Slot(attrs, 27))(attrs, &key, _manager), "configure the encoder");
            }
            Check(MFCreateSinkWriterFromURL(_path, IntPtr.Zero, attrs, out _writer), "create the video file");

            Check(MFCreateMediaType(out outType), "start the video encoder");
            SetGuid(outType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            SetGuid(outType, MF_MT_SUBTYPE, MFVideoFormat_H264);
            SetU32(outType, MF_MT_AVG_BITRATE, RecordingMath.Bitrate(w, h));
            SetU32(outType, MF_MT_INTERLACE_MODE, 2);
            SetU32(outType, MF_MT_MPEG2_PROFILE, 100 /* High */);
            SetU64(outType, MF_MT_FRAME_SIZE, ((ulong)w << 32) | (uint)h);
            SetU64(outType, MF_MT_FRAME_RATE, ((ulong)RecordingMath.Fps << 32) | 1);
            SetU64(outType, MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
            uint stream;
            Check(Call(_writer, 3, outType, &stream), "start the video encoder");
            _stream = stream;

            Check(MFCreateMediaType(out inType), "start the video encoder");
            SetGuid(inType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            SetGuid(inType, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
            SetU32(inType, MF_MT_INTERLACE_MODE, 2);
            SetU32(inType, MF_MT_DEFAULT_STRIDE, (uint)(w * 4));
            SetU64(inType, MF_MT_FRAME_SIZE, ((ulong)w << 32) | (uint)h);
            SetU64(inType, MF_MT_FRAME_RATE, ((ulong)RecordingMath.Fps << 32) | 1);
            SetU64(inType, MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);

            Check(MFCreateAttributes(out enc, 3), "start the video encoder");
            SetU32(enc, CODECAPI_AVEncCommonRateControlMode, 3 /* Quality */);
            SetU32(enc, CODECAPI_AVEncCommonQuality, 70);
            SetU32(enc, CODECAPI_AVEncMPVGOPSize, RecordingMath.Fps * 10);
            var hrEnc = Call(_writer, 4, _stream, inType, enc);
            if (hrEnc != 0)
            {
                Log.Write($"recording: encoder rejected quality mode (0x{hrEnc:X8}), using bitrate mode");
                Check(Call(_writer, 4, _stream, inType, IntPtr.Zero), "start the video encoder");
            }
            if (_systemSound || _microphone) AddAudioStream();
            Check(Call(_writer, 5), "start the video encoder");
        }
        finally
        {
            Release(ref enc);
            Release(ref inType);
            Release(ref outType);
            Release(ref attrs);
        }
    }

    private void AddAudioStream()
    {
        IntPtr outType = IntPtr.Zero, inType = IntPtr.Zero;
        try
        {
            Check(MFCreateMediaType(out outType), "start the sound encoder");
            SetGuid(outType, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            SetGuid(outType, MF_MT_SUBTYPE, MFAudioFormat_AAC);
            SetU32(outType, MF_MT_AUDIO_NUM_CHANNELS, AudioMixer.Channels);
            SetU32(outType, MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioMixer.Rate);
            SetU32(outType, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            SetU32(outType, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 12000 /* 96 kbps */);
            uint stream;
            Check(Call(_writer, 3, outType, &stream), "start the sound encoder");
            _audioStream = stream;

            Check(MFCreateMediaType(out inType), "start the sound encoder");
            SetGuid(inType, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            SetGuid(inType, MF_MT_SUBTYPE, MFAudioFormat_PCM);
            SetU32(inType, MF_MT_AUDIO_NUM_CHANNELS, AudioMixer.Channels);
            SetU32(inType, MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioMixer.Rate);
            SetU32(inType, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            SetU32(inType, MF_MT_AUDIO_BLOCK_ALIGNMENT, AudioMixer.Channels * 2);
            SetU32(inType, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, AudioMixer.Rate * AudioMixer.Channels * 2);
            Check(Call(_writer, 4, _audioStream, inType, IntPtr.Zero), "start the sound encoder");
        }
        finally
        {
            Release(ref inType);
            Release(ref outType);
        }
    }

    private void Cleanup()
    {
        _audio?.Release();
        _audio = null;
        if (_frames > 0 && _staging != IntPtr.Zero)
        {
            try
            {
                if (_gpu) CallVoid(_context, 47, _staging, _accum);
                Mapped mapped;
                Check(Call(_context, 14, _staging, 0u, 1u, 0u, &mapped), "read the last frame");
                var bmp = new Bitmap(_rect.Width, _rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                var data = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                for (int y = 0; y < _rect.Height; y++)
                    Buffer.MemoryCopy((byte*)mapped.Data + (long)y * mapped.RowPitch, (byte*)data.Scan0 + (long)y * data.Stride, data.Stride, _rect.Width * 4);
                bmp.UnlockBits(data);
                CallVoid(_context, 15, _staging, 0u);
                _lastFrame = bmp;
            }
            catch (Exception ex) { Log.Write($"recording thumbnail failed: {ex.Message}"); }
        }
        foreach (var s in _sources)
        {
            Release(ref s.Dup);
            Release(ref s.Output1);
        }
        _sources.Clear();
        Release(ref _writer);
        Release(ref _staging);
        Release(ref _accum);
        Release(ref _manager);
        Release(ref _context);
        Release(ref _device);
        if (_memDc != IntPtr.Zero)
        {
            SelectObject(_memDc, _oldBmp);
            DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; _bits = null; }
        if (_mfStarted) { MFShutdown(); _mfStarted = false; }
        _clock.Stop();
        Log.Write($"recording ended: {_frames} frames, {TimeSpan.FromTicks(_lastTimestamp).TotalSeconds:0.0} s");
    }

    // ---------- COM plumbing ----------

    private static void* Slot(IntPtr p, int i) => (*(void***)p)[i];
    private static int Call(IntPtr p, int i) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(p, i))(p);
    private static int Call(IntPtr p, int i, uint a) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(p, i))(p, a);
    private static int Call(IntPtr p, int i, long a) => ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(p, i))(p, a);
    private static int Call(IntPtr p, int i, IntPtr a) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(p, i))(p, a);
    private static int Call(IntPtr p, int i, void* a) => ((delegate* unmanaged[Stdcall]<IntPtr, void*, int>)Slot(p, i))(p, a);
    private static int Call(IntPtr p, int i, Guid* a, IntPtr* b) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(p, i))(p, a, b);
    private static int Call(IntPtr p, int i, uint a, IntPtr* b) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(p, i))(p, a, b);
    private static int Call(IntPtr p, int i, IntPtr a, IntPtr* b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(p, i))(p, a, b);
    private static int Call(IntPtr p, int i, IntPtr a, uint* b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Slot(p, i))(p, a, b);
    private static int Call(IntPtr p, int i, uint a, IntPtr b) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Slot(p, i))(p, a, b);
    private static int Call(IntPtr p, int i, byte** a, uint* b, uint* c) => ((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(p, i))(p, a, b, c);
    private static int Call(IntPtr p, int i, uint a, FrameInfo* b, IntPtr* c) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, FrameInfo*, IntPtr*, int>)Slot(p, i))(p, a, b, c);
    private static int Call(IntPtr p, int i, uint a, IntPtr b, IntPtr c) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(p, i))(p, a, b, c);
    private static int Call(IntPtr p, int i, TextureDesc* a, IntPtr b, IntPtr* c) => ((delegate* unmanaged[Stdcall]<IntPtr, TextureDesc*, IntPtr, IntPtr*, int>)Slot(p, i))(p, a, b, c);
    private static int Call(IntPtr p, int i, IntPtr a, uint b, uint c, uint d, Mapped* e) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, Mapped*, int>)Slot(p, i))(p, a, b, c, d, e);
    private static int Call(IntPtr p, int i, int a, IntPtr* b) => ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Slot(p, i))(p, a, b);
    private static int Call(IntPtr p, int i, IntPtr a, uint b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)Slot(p, i))(p, a, b);
    private static void CallVoid(IntPtr p, int i, IntPtr a, IntPtr b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(p, i))(p, a, b);
    private static void CallVoid(IntPtr p, int i, IntPtr a, uint b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(p, i))(p, a, b);
    private static void CallVoid(IntPtr p, int i, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, Box* box) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)Slot(p, i))(p, dst, dstSub, x, y, z, src, srcSub, box);

    private static void Release(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        Call(p, 2);
        p = IntPtr.Zero;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"Couldn't {what} (0x{hr:X8}).", hr);
    }

    private static void SetU32(IntPtr attrs, Guid key, uint v) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(attrs, 21))(attrs, &key, v), "configure the encoder");
    private static void SetU64(IntPtr attrs, Guid key, ulong v) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Slot(attrs, 22))(attrs, &key, v), "configure the encoder");
    private static void SetGuid(IntPtr attrs, Guid key, Guid v) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(attrs, 24))(attrs, &key, &v), "configure the encoder");

    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IDXGISurface1 = new("4ae63092-6327-4c1b-80ae-bfe12ea32b86");
    private static readonly Guid IID_IMF2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
    private static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    private static readonly Guid MF_SINK_WRITER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    private static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced, ProtectedContentMaskedOut;
        public int PointerX, PointerY, PointerVisible;
        public uint TotalMetadataBufferSize, PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDesc
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int Attached;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped
    {
        public IntPtr Data;
        public uint RowPitch, DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size, Flags;
        public IntPtr Cursor;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon, XHotspot, YHotspot;
        public IntPtr Mask, Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(Guid* riid, out IntPtr factory);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, int* levels, uint levelCount, uint sdkVersion, out IntPtr device, out int level, out IntPtr context);
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IntPtr attrs, uint size);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint length, out IntPtr buffer);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] private static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr manager);
    [DllImport("mfplat.dll")] private static extern int MFCreateDXGISurfaceBuffer(Guid* riid, IntPtr surface, uint subresource, int bottomUp, out IntPtr buffer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attrs, out IntPtr writer);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
