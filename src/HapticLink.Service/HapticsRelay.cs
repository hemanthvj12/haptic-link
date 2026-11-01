using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace HapticLink.Service;

/// <summary>
/// Relays a game's DualSense haptics to a Bluetooth pad. Games play them as audio on channels 3-4 of the virtual pad's
/// speaker endpoint; this records that endpoint (WASAPI loopback), low-passes and decimates channels 3-4 from 48 kHz to
/// 3 kHz signed 8-bit, and sends one report 0x32 (32 frames, 10.67 ms) per tick of a steady clock.
/// What made it feel right on the owner's pad (Stellar Blade, 2026-11-01):
///   - a steady clock instead of sending in bursts as audio arrives (bursts made it late and uneven);
///   - silent blocks are sent too while the game's stream runs: a pad that heard nothing for a moment swallowed the
///     start of the next effect, so short effects (menu clicks) mostly vanished;
///   - at most two blocks wait (a fixed limit of one dropped ~8% of blocks to timing jitter).
/// Latency (2026-11-02): the clock's phase follows the blocks instead of running free. Blocks arrive unevenly (the
/// audio engine hands over 10 ms chunks, a report holds 10.67 ms), so the clock keeps the earliest send of each
/// half-second just Margin after its block arrived, nudging itself earlier while every block waits longer than that.
/// A block that misses its tick is sent the moment it arrives and the clock moves later, so a late block costs a few
/// milliseconds instead of a gap; a block is dropped only when a whole extra block keeps waiting.
/// Every 30 s of haptics the log gets "haptics timing": the delay from the audio engine's mix to the report write.
/// </summary>
internal sealed unsafe class HapticsRelay : IDisposable
{
    private const int MaxQueue = 2, Window = 48;
    private const long Margin = 15_000, Step = 10_000, Backoff = 10_000; // 100 ns units: 1.5 ms, 1 ms, 1 ms
    private const double Period = 10_000_000.0 * 32 / 3000; // one report, 100 ns units
    private const long StatsEvery = 300_000_000; // 30 s

    private readonly record struct Block(sbyte[] Data, long Mixed, long Arrived);

    private readonly string _endpointId;
    private readonly Func<ReadOnlyMemory<byte>, bool> _write;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<Block> _queue = new();
    private readonly AutoResetEvent _arrived = new(false);
    private readonly CancellationTokenSource _cts = new();
    private long _lastArrival;
    private Thread? _capture, _sender;

    public HapticsRelay(string endpointId, Func<ReadOnlyMemory<byte>, bool> write, Action<string> log)
    {
        _endpointId = endpointId;
        _write = write;
        _log = log;
    }

    public void Start()
    {
        _capture = new Thread(CaptureLoop) { IsBackground = true, Name = "haptics capture", Priority = ThreadPriority.AboveNormal };
        _sender = new Thread(SendLoop) { IsBackground = true, Name = "haptics send", Priority = ThreadPriority.Highest };
        _capture.Start();
        _sender.Start();
    }

    /// <summary>
    /// Records until stopped. Opening can fail for a moment right after the service shows the endpoint, and Windows can
    /// invalidate a running stream (device or format changes): both just retry every second. Failing quietly once was
    /// a whole session without haptics (2026-11-01).
    /// </summary>
    private void CaptureLoop()
    {
        string? lastError = null;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                CaptureSession();
                return; // stopped
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException)
            {
                if (ex.Message != lastError) _log($"haptics: {ex.Message}; retrying");
                lastError = ex.Message;
            }
            _cts.Token.WaitHandle.WaitOne(1000);
        }
    }

    private void CaptureSession()
    {
        {
            var enumerator = (IMMDeviceEnumerator)ComObjects.Create(ComObjects.MMDeviceEnumerator);
            Check(enumerator.GetDevice(_endpointId, out var device), "open the pad speaker");
            var iid = typeof(IAudioClient).GUID;
            Check(device.Activate(ref iid, 0x17, IntPtr.Zero, out var o), "activate the pad speaker");
            var client = (IAudioClient)o;
            Check(client.GetMixFormat(out IntPtr format), "read the pad speaker's format");
            int channels = *(ushort*)(format + 2), bits = *(ushort*)(format + 14);
            if (channels < 4) throw new InvalidOperationException($"the pad speaker has {channels} channels, not 4");
            // The buffer only bounds how far behind we may fall; draining it every millisecond keeps capture delay at
            // about one engine period.
            Check(client.Initialize(0, 0x00020000 /* loopback */, 300_000, 0, format, IntPtr.Zero), "start recording the pad speaker");
            var ciid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref ciid, out var co), "get the capture client");
            var capture = (IAudioCaptureClient)co;
            Check(client.Start(), "start recording");
            int rate = *(int*)(format + 4);
            _log($"haptics: recording the pad speaker ({channels} ch, {bits}-bit, {EnginePeriod(client, format, rate)})");

            var decimator = new Decimator();
            var block = new sbyte[DualSenseReports.HapticsSamples];
            int fill = 0;
            bool loggedPacket = false;
            while (!_cts.IsCancellationRequested)
            {
                Thread.Sleep(1);
                int hr;
                while ((hr = capture.GetNextPacketSize(out uint packet)) == 0 && packet > 0)
                {
                    Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out ulong qpc), "read the pad speaker");
                    if (!loggedPacket)
                    {
                        _log($"haptics: first packet {frames} frames ({frames * 1000.0 / rate:0.##} ms), timestamps {(qpc != 0 ? "on" : "off")}");
                        loggedPacket = true;
                    }
                    bool silent = (flags & 0x2) != 0;
                    for (int f = 0; f < frames; f++)
                    {
                        float l = 0, r = 0;
                        if (!silent)
                        {
                            if (bits == 32) { float* s = (float*)data + f * channels; l = s[2]; r = s[3]; }
                            else { short* s = (short*)data + f * channels; l = s[2] / 32768f; r = s[3] / 32768f; }
                        }
                        if (!decimator.Push(l, r, out float dl, out float dr)) continue;
                        block[fill++] = ToS8(dl);
                        block[fill++] = ToS8(dr);
                        if (fill == block.Length)
                        {
                            // qpc is when the engine mixed the packet's first frame (100 ns units, same clock as Now).
                            long now = Now();
                            long mixed = qpc != 0 ? (long)qpc + (long)(f * 10_000_000.0 / rate) : 0;
                            _queue.Enqueue(new Block((sbyte[])block.Clone(), mixed, now));
                            Volatile.Write(ref _lastArrival, now);
                            _arrived.Set();
                            fill = 0;
                        }
                    }
                    capture.ReleaseBuffer(frames);
                }
                Check(hr, "keep recording the pad speaker"); // e.g. AUDCLNT_E_DEVICE_INVALIDATED: start over
            }
            client.Stop();
        }
    }

    // One report per 10.667 ms on a high-resolution timer, while blocks keep coming (the game's stream is open). The
    // timer's phase follows the blocks: see the summary.
    private void SendLoop()
    {
        IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0x2 /* high resolution */, 0x1F0003);
        var report = new byte[DualSenseReports.HapticsLength];
        byte counter = 0;
        var stats = new Stats();
        long windowMin = long.MaxValue, statsFrom = Now();
        int windowCount = 0;
        double next = Now();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                next += Period;
                long wait = (long)next - Now();
                if (wait > 0)
                {
                    long due = -wait;
                    SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                    WaitForSingleObject(timer, 100);
                }
                else if (wait < -(long)(Period * 4)) next = Now(); // a stall: resync instead of catching up

                if (stats.Sent > 0 && Now() - statsFrom >= StatsEvery)
                {
                    _log($"haptics timing: {stats}");
                    stats = new Stats();
                    statsFrom = Now();
                }

                while (_queue.Count > MaxQueue && _queue.TryDequeue(out _)) stats.Dropped++;
                bool onTime = _queue.TryDequeue(out var block);
                if (!onTime)
                {
                    // Nothing for a while: the game's stream is closed, so stay quiet.
                    if (Now() - Volatile.Read(ref _lastArrival) > (long)(Period * 3)) continue;
                    if (!WaitForBlock((long)(Period / 2), out block)) continue;
                    // Late: send it now and move the clock later so the next ones make it.
                    next = Now() + Backoff;
                    stats.Late++;
                    windowMin = long.MaxValue;
                    windowCount = 0;
                }

                DualSenseReports.Haptics(block.Data, counter++, report);
                long before = Now();
                _write(report);
                long after = Now();
                stats.Add(before - block.Arrived, block.Mixed != 0, after - block.Mixed, after - before);
                if (!onTime) continue;

                windowMin = Math.Min(windowMin, before - block.Arrived);
                if (++windowCount < Window) continue;
                long slack = windowMin - Margin;
                if (slack > (long)Period) { if (_queue.TryDequeue(out _)) stats.Dropped++; } // a whole extra block waits
                else if (slack > 0) next -= Math.Min(slack, Step);
                windowMin = long.MaxValue;
                windowCount = 0;
            }
        }
        finally { CloseHandle(timer); }
    }

    private bool WaitForBlock(long timeout, out Block block)
    {
        long deadline = Now() + timeout;
        while (!_queue.TryDequeue(out block))
        {
            long left = deadline - Now();
            if (left <= 0) return false;
            _arrived.WaitOne(Math.Max(1, (int)(left / 10_000)));
        }
        return true;
    }

    private sealed class Stats
    {
        public int Sent, Late, Dropped;
        private long _waitSum, _waitMax, _mixSum, _mixMin, _mixMax, _mixCount, _writeSum, _writeMax;

        // mixToWrite can be negative: the endpoint's timestamps can run ahead of when we read its audio.
        public void Add(long waited, bool hasMix, long mixToWrite, long write)
        {
            Sent++;
            _waitSum += waited; _waitMax = Math.Max(_waitMax, waited);
            _writeSum += write; _writeMax = Math.Max(_writeMax, write);
            if (!hasMix) return;
            if (_mixCount++ == 0) _mixMin = _mixMax = mixToWrite;
            _mixSum += mixToWrite;
            _mixMin = Math.Min(_mixMin, mixToWrite);
            _mixMax = Math.Max(_mixMax, mixToWrite);
        }

        private static string Ms(double ticks) => (ticks / 10_000).ToString("0.0");

        public override string ToString() =>
            $"{Sent} reports; mix to write avg {(_mixCount > 0 ? $"{Ms((double)_mixSum / _mixCount)} ms ({Ms(_mixMin)} to {Ms(_mixMax)})" : "?")}; " +
            $"queue wait avg {Ms((double)_waitSum / Sent)}, max {Ms(_waitMax)}; write avg {Ms((double)_writeSum / Sent)}, max {Ms(_writeMax)}; " +
            $"{Late} late, {Dropped} dropped";
    }

    // What the audio engine runs at: a shorter engine period would hand over smaller chunks sooner.
    private static string EnginePeriod(IAudioClient client, IntPtr format, int rate)
    {
        string text = client.GetDevicePeriod(out long def, out long min) == 0
            ? $"device period {def / 10_000.0:0.##} ms, min {min / 10_000.0:0.##}" : "device period ?";
        if (client is IAudioClient3 c3
            && c3.GetSharedModeEnginePeriod(format, out uint d, out _, out uint lo, out uint hi) == 0
            && c3.GetCurrentSharedModeEnginePeriod(out IntPtr current, out uint now) == 0)
        {
            Marshal.FreeCoTaskMem(current);
            text += $"; engine {now * 1000.0 / rate:0.##} ms now, shared {lo * 1000.0 / rate:0.##}-{hi * 1000.0 / rate:0.##} (default {d * 1000.0 / rate:0.##})";
        }
        return text;
    }

    private static sbyte ToS8(float v) => (sbyte)Math.Clamp((int)MathF.Round(v * 127f), -127, 127);

    private static long Now() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    private static void Check(int hr, string what)
    {
        if (hr != 0) throw new COMException($"couldn't {what} (0x{hr:X8})", hr);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _capture?.Join(1000);
        _sender?.Join(1000);
        _arrived.Dispose();
    }

    // 48 kHz → 3 kHz: Hann-windowed sinc low-pass (1.2 kHz, 96 taps, unity gain at DC), keeping every 16th frame.
    private sealed class Decimator
    {
        private const int Taps = 96, Factor = 16;
        private readonly float[] _taps = new float[Taps], _l = new float[Taps], _r = new float[Taps];
        private int _pos, _phase;

        public Decimator()
        {
            double fc = 1200.0 / 48000.0, mid = (Taps - 1) / 2.0, sum = 0;
            for (int i = 0; i < Taps; i++)
            {
                double x = i - mid;
                double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
                _taps[i] = (float)(sinc * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Taps - 1))));
                sum += _taps[i];
            }
            for (int i = 0; i < Taps; i++) _taps[i] /= (float)sum;
        }

        public bool Push(float l, float r, out float outL, out float outR)
        {
            _l[_pos] = l; _r[_pos] = r;
            _pos = (_pos + 1) % Taps;
            outL = outR = 0;
            if (++_phase < Factor) return false;
            _phase = 0;
            for (int i = 0; i < Taps; i++)
            {
                int k = (_pos + i) % Taps;
                outL += _l[k] * _taps[i];
                outR += _r[k] * _taps[i];
            }
            return true;
        }
    }


    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IntPtr device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    // IAudioClient3 repeats IAudioClient's and IAudioClient2's methods first, so the vtable lines up.
    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient3
    {
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        [PreserveSig] int IsOffloadCapable(int category, out int capable);
        [PreserveSig] int SetClientProperties(IntPtr properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long min, out long max);
        [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultFrames, out uint fundamentalFrames, out uint minFrames, out uint maxFrames);
        [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint frames);
        [PreserveSig] int InitializeSharedAudioStream(int flags, uint periodFrames, IntPtr format, IntPtr session);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);
    [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr routine, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
