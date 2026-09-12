using System.Runtime.InteropServices;

namespace MusicAssistant.Sendspin;

/// <summary>
/// Shared-mode WASAPI render stream on the default output device, event driven.
///
/// A render thread wakes on every device period, asks the renderer callback to
/// fill the free part of the buffer and tells it the exact local time the first
/// frame it writes will leave the device, computed from IAudioClock (device
/// position stamped with QueryPerformanceCounter). That timestamp is what lets
/// the scheduler hit server timestamps to the millisecond without guessing
/// pipeline latency. Plain COM interop, no packages.
/// </summary>
/// <remarks>
/// @link https://learn.microsoft.com/windows/win32/coreaudio/rendering-a-stream
/// @link https://learn.microsoft.com/windows/win32/api/audioclient/nn-audioclient-iaudioclock
/// </remarks>
public sealed class WasapiOutput : IDisposable
{
    /// <summary>Fill `frames` interleaved float frames into `buffer`; the first frame plays at `firstFrameTimeUs` (local clock).</summary>
    public delegate void RenderCallback(Span<float> buffer, int frames, long firstFrameTimeUs);

    public int SampleRate { get; private set; }
    public int Channels   { get; private set; }

    private IAudioClient?       client;
    private IAudioRenderClient? render;
    private IAudioClock?        clock;
    private AutoResetEvent?     wake;
    private Thread?             thread;
    private volatile bool       running;
    private uint                bufferFrames;
    private long                framesWritten;
    private ulong               clockFrequency;
    private int                 bitsPerSample;
    private bool                isFloat;
    private readonly RenderCallback callback;
    private float[]             scratch = [];

    public WasapiOutput(RenderCallback callback)
    {
        this.callback = callback;
    }

    /// <summary>Open the default render device in its mix format and start the render thread.</summary>
    public void Start()
    {
        if (running) return;

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice? device = null;
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device);
            var iid = typeof(IAudioClient).GUID;
            device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var clientObject);
            client = (IAudioClient)clientObject;
        }
        finally
        {
            // The enumerator and device are only needed to reach the client; release them now, not at GC
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }

        client.GetMixFormat(out var formatPtr);
        try
        {
            ReadFormat(formatPtr);
            // 100ms of buffer: room for a late wake-up; the device clock, not the buffer size, drives timing
            client.Initialize(ShareModeShared, StreamFlagsEventCallback, 100 * 10_000, 0, formatPtr, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeCoTaskMem(formatPtr);
        }

        client.GetBufferSize(out bufferFrames);
        wake = new AutoResetEvent(false);
        client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle());

        var renderIid = typeof(IAudioRenderClient).GUID;
        client.GetService(ref renderIid, out var renderObject);
        render = (IAudioRenderClient)renderObject;

        var clockIid = typeof(IAudioClock).GUID;
        client.GetService(ref clockIid, out var clockObject);
        clock = (IAudioClock)clockObject;
        clock.GetFrequency(out clockFrequency);

        scratch = new float[bufferFrames * Channels];
        framesWritten = 0;
        running = true;
        thread = new Thread(RenderLoop) { IsBackground = true, Name = "Sendspin render", Priority = ThreadPriority.Highest };
        thread.Start();
    }

    public void Stop()
    {
        if (!running) return;
        running = false;
        wake?.Set();
        // The render thread owns the COM objects and tears them down in its own finally, so a slow in-flight
        // WASAPI call is never released out from under it. If the join times out, cleanup happens when the
        // thread finally exits, not here.
        thread?.Join(3000);
    }

    private void RenderLoop()
    {
        var taskIndex = 0u;
        var mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);   // scheduler priority meant for audio render threads
        try
        {
            Prime();
            client!.Start();
            while (running)
            {
                if (!wake!.WaitOne(500)) continue;
                if (!running) break;
                FillOnce();
            }
        }
        catch (Exception ex)
        {
            App.Log("Speaker output stopped: " + ex.Message);
            running = false;
        }
        finally
        {
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
            try { client?.Stop(); } catch (Exception) { }
            Release();   // the thread that used the COM objects is the one that releases them
        }
    }

    /// <summary>Fill the whole buffer with silence before starting so the clock runs from a known state.</summary>
    private void Prime()
    {
        render!.GetBuffer(bufferFrames, out var data);
        render.ReleaseBuffer(bufferFrames, BufferFlagsSilent);
        framesWritten += bufferFrames;
    }

    private void FillOnce()
    {
        client!.GetCurrentPadding(out var padding);
        var frames = (int)(bufferFrames - padding);
        if (frames <= 0) return;

        var firstFrameTime = FirstFrameTimeUs(padding);
        var span = scratch.AsSpan(0, frames * Channels);
        span.Clear();
        callback(span, frames, firstFrameTime);

        render!.GetBuffer((uint)frames, out var data);
        WriteSamples(data, frames * Channels);
        render.ReleaseBuffer((uint)frames, 0);
        framesWritten += frames;
    }

    /// <summary>Local time at which the next frame written will play, from the device clock's last (position, QPC) pair.</summary>
    private long FirstFrameTimeUs(uint padding)
    {
        try
        {
            clock!.GetPosition(out var position, out var qpc);
            if (clockFrequency > 0 && qpc > 0)
            {
                var playedFrames = (double)position / clockFrequency * SampleRate;
                var playedAtUs   = (long)(qpc / 10);   // 100ns units
                var ahead        = framesWritten - playedFrames;   // frames queued between the playing frame and the next write
                return playedAtUs + (long)(ahead / SampleRate * 1_000_000.0);
            }
        }
        catch (COMException) { }
        // Clock not running yet: the queued frames are all that stand between now and the next write
        return Clock.NowUs() + (long)(padding / (double)SampleRate * 1_000_000.0);
    }

    private short[] shortScratch = [];
    private int[]   intScratch   = [];

    /// <summary>Copy the first `count` samples of `scratch` into the device buffer in its own sample format.</summary>
    private void WriteSamples(IntPtr data, int count)
    {
        if (isFloat)
        {
            Marshal.Copy(scratch, 0, data, count);
            return;
        }
        if (bitsPerSample == 16)
        {
            if (shortScratch.Length < count) shortScratch = new short[count];
            for (var i = 0; i < count; i++) shortScratch[i] = (short)Math.Clamp(scratch[i] * 32767f, -32768f, 32767f);
            Marshal.Copy(shortScratch, 0, data, count);
            return;
        }
        if (intScratch.Length < count) intScratch = new int[count];   // 32-bit integer PCM
        for (var i = 0; i < count; i++) intScratch[i] = (int)Math.Clamp(scratch[i] * 2147483647.0, int.MinValue, int.MaxValue);
        Marshal.Copy(intScratch, 0, data, count);
    }

    private void ReadFormat(IntPtr format)
    {
        var tag = (ushort)Marshal.ReadInt16(format, 0);
        Channels      = Marshal.ReadInt16(format, 2);
        SampleRate    = Marshal.ReadInt32(format, 4);
        bitsPerSample = Marshal.ReadInt16(format, 14);
        if (tag == FormatExtensible)
        {
            var subFormat = Marshal.PtrToStructure<Guid>(format + 24);
            isFloat = subFormat == SubtypeIeeeFloat;
        }
        else
        {
            isFloat = tag == FormatIeeeFloat;
        }
        if (!isFloat && bitsPerSample is not (16 or 32)) throw new NotSupportedException($"Output format {bitsPerSample}-bit integer is not supported");
    }

    private void Release()
    {
        if (render is not null) Marshal.ReleaseComObject(render);
        if (clock  is not null) Marshal.ReleaseComObject(clock);
        if (client is not null) Marshal.ReleaseComObject(client);
        render = null; clock = null; client = null;
        wake?.Dispose(); wake = null;
    }

    public void Dispose() => Stop();

    // =========================================================================
    // COM
    // =========================================================================

    private const uint ClsCtxAll = 23;
    private const int  ShareModeShared = 0;
    private const int  StreamFlagsEventCallback = 0x00040000;
    private const int  BufferFlagsSilent = 0x2;
    private const ushort FormatIeeeFloat = 3, FormatExtensible = 0xFFFE;
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private enum EDataFlow { Render = 0 }
    private enum ERole { Console = 0, Multimedia = 1 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        void GetBuffer(uint frames, out IntPtr data);
        void ReleaseBuffer(uint frames, int flags);
    }

    [ComImport, Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClock
    {
        void GetFrequency(out ulong frequency);
        void GetPosition(out ulong position, out ulong qpcPosition);
        void GetCharacteristics(out uint characteristics);
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll")] private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
