using System.Runtime.InteropServices;

namespace MusicAssistant.Sendspin;

/// <summary>
/// Shared-mode WASAPI render stream on the default output device, event driven.
///
/// Follows the default device: when Windows switches the output (headphones
/// plugged in, a speaker picked in the volume flyout, the device unplugged),
/// the stream reopens on the new default in the format it started with, and
/// Windows converts to the new device's format, so the decoder and scheduler
/// carry on without knowing.
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
/// @link https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient
/// </remarks>
public sealed class WasapiOutput : IDisposable
{
    /// <summary>Fill `frames` interleaved float frames into `buffer`; the first frame plays at `firstFrameTimeUs` (local clock).</summary>
    /// <param name="buffer">The pre-cleared interleaved buffer to fill.</param>
    /// <param name="frames">Number of frames to fill.</param>
    /// <param name="firstFrameTimeUs">Local time the first frame leaves the device, in microseconds.</param>
    public delegate void RenderCallback(Span<float> buffer, int frames, long firstFrameTimeUs);

    /// <summary>The device mix format's sample rate; set by <see cref="Start"/>.</summary>
    public int SampleRate { get; private set; }

    /// <summary>The device mix format's channel count; set by <see cref="Start"/>.</summary>
    public int Channels   { get; private set; }

    /// <summary>Raised when the render thread dies unexpectedly (driver reset, or no output device left to move to), never on a normal Stop(). Handlers must not block or call Stop()/Dispose() inline, since this may fire on the render thread mid-teardown.</summary>
    public event Action? Failed;

    /// <summary>AUDCLNT_E_DEVICE_INVALIDATED: the device the stream ran on was removed or disabled.</summary>
    private const int DeviceInvalidated = unchecked((int)0x88890004);

    /// <summary>Serializes starting, stopping and device switches, which arrive from the app and from Windows' callbacks.</summary>
    private readonly object lifecycle = new();

    /// <summary>Whether the owner wants audio running: set by Start, cleared by Stop, so a late device switch opens nothing.</summary>
    private bool wanted;

    private IMMDeviceEnumerator? watchEnumerator;
    private DefaultDeviceWatcher? watcher;

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

    /// <summary>Creates an output that pulls audio from a callback; the device opens on <see cref="Start"/>.</summary>
    /// <param name="callback">Called on the render thread to fill each period's frames.</param>
    public WasapiOutput(RenderCallback callback)
    {
        this.callback = callback;
    }

    /// <summary>Open the default render device in its mix format, start the render thread and follow default device changes.</summary>
    public void Start()
    {
        lock (lifecycle)
        {
            wanted = true;
            if (!running)
            {
                Open(keepFormat: false);
            }
            Watch();
        }
    }

    /// <summary>Signals the render thread to stop and waits up to 3 seconds for it; the thread releases the device itself.</summary>
    public void Stop()
    {
        lock (lifecycle)
        {
            wanted = false;
            Halt();
        }
    }

    /// <summary>Reopens the stream on the current default device, keeping the format the decoder and scheduler use.</summary>
    /// <param name="why">What triggered the switch, for the log.</param>
    private void SwitchDevice(string why)
    {
        lock (lifecycle)
        {
            if (!wanted)
            {
                return;
            }

            App.Debug($"Speaker output: {why}, moving to the default device");
            Halt();
            try
            {
                Open(keepFormat: true);
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                // No usable output left (the last device went away): the owner decides what a dead output means.
                App.Log($"Speaker output couldn't move to the default device: {ex.Message}");
                Failed?.Invoke();
            }
        }
    }

    /// <summary>Opens the default device and starts the render thread. Caller holds <see cref="lifecycle"/>.</summary>
    /// <param name="keepFormat">Reopen in the current <see cref="SampleRate"/> and <see cref="Channels"/> as float, letting Windows convert, instead of the device's mix format.</param>
    private void Open(bool keepFormat)
    {
        // A prior Stop() only joins for 3s, so its render thread may still be tearing down the COM objects.
        // Never reuse the COM fields until that thread has fully exited, or its finally would release the new session's objects.
        thread?.Join();

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice? device = null;
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device);
            Guid iid = typeof(IAudioClient).GUID;
            device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object? clientObject);
            client = (IAudioClient)clientObject;
        }
        finally
        {
            // The enumerator and device are only needed to reach the client; release them now, not at GC.
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }

        // No render thread runs yet, so any failure here must release the COM objects itself; Stop() would no-op with running still false.
        try
        {
            if (keepFormat)
            {
                nint formatPtr = FloatFormat(SampleRate, Channels);
                try
                {
                    ReadFormat(formatPtr);
                    client.Initialize(ShareModeShared, StreamFlagsEventCallback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality, 100 * 10_000, 0, formatPtr, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeHGlobal(formatPtr);
                }
            }
            else
            {
                client.GetMixFormat(out nint formatPtr);
                try
                {
                    ReadFormat(formatPtr);
                    // 100ms of buffer: room for a late wake-up; the device clock, not the buffer size, drives timing.
                    client.Initialize(ShareModeShared, StreamFlagsEventCallback, 100 * 10_000, 0, formatPtr, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(formatPtr);
                }
            }

            client.GetBufferSize(out bufferFrames);
            wake = new AutoResetEvent(false);
            client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle());

            Guid renderIid = typeof(IAudioRenderClient).GUID;
            client.GetService(ref renderIid, out object? renderObject);
            render = (IAudioRenderClient)renderObject;

            Guid clockIid = typeof(IAudioClock).GUID;
            client.GetService(ref clockIid, out object? clockObject);
            clock = (IAudioClock)clockObject;
            clock.GetFrequency(out clockFrequency);
        }
        catch
        {
            Release();
            throw;
        }

        scratch = new float[bufferFrames * Channels];
        framesWritten = 0;
        running = true;
        thread = new Thread(RenderLoop) { IsBackground = true, Name = "Sendspin render", Priority = ThreadPriority.Highest };
        thread.Start();
    }

    /// <summary>Stops the render thread, waiting up to 3 seconds. Caller holds <see cref="lifecycle"/>.</summary>
    private void Halt()
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
        uint taskIndex = 0u;
        // Scheduler priority meant for audio render threads.
        nint mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
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
        catch (COMException ex) when (ex.HResult == DeviceInvalidated)
        {
            // The device went away; Windows picks a new default, so move there rather than fail. Queued, because the
            // switch joins this thread.
            running = false;
            ThreadPool.QueueUserWorkItem(_ => SwitchDevice("device removed"));
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Speaker output stopped: {ex.Message}");
            running = false;
            // Fire before the finally releases COM; the handler must only signal, not join this thread.
            Failed?.Invoke();
        }
        finally
        {
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
            try
            {
                client?.Stop();
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
            }
            // The thread that used the COM objects is the one that releases them.
            Release();
        }
    }

    /// <summary>Fill the whole buffer with silence before starting so the clock runs from a known state.</summary>
    private void Prime()
    {
        render!.GetBuffer(bufferFrames, out nint data);
        render.ReleaseBuffer(bufferFrames, BufferFlagsSilent);
        framesWritten += bufferFrames;
    }

    private void FillOnce()
    {
        client!.GetCurrentPadding(out uint padding);
        int frames = (int)(bufferFrames - padding);
        if (frames <= 0) return;

        long firstFrameTime = FirstFrameTimeUs(padding);
        Span<float> span = scratch.AsSpan(0, frames * Channels);
        span.Clear();
        callback(span, frames, firstFrameTime);

        render!.GetBuffer((uint)frames, out nint data);
        WriteSamples(data, frames * Channels);
        render.ReleaseBuffer((uint)frames, 0);
        framesWritten += frames;
    }

    /// <summary>Local time at which the next frame written will play, from the device clock's last (position, QPC) pair.</summary>
    private long FirstFrameTimeUs(uint padding)
    {
        try
        {
            clock!.GetPosition(out ulong position, out ulong qpc);
            if ((clockFrequency > 0) && (qpc > 0))
            {
                double playedFrames = (double)position / clockFrequency * SampleRate;
                // 100ns units.
                long playedAtUs   = (long)(qpc / 10);
                // Frames queued between the playing frame and the next write.
                double ahead        = framesWritten - playedFrames;
                return playedAtUs + (long)(ahead / SampleRate * 1_000_000.0);
            }
        }
        catch (COMException)
        {
        }
        // Clock not running yet: the queued frames are all that stand between now and the next write.
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
            for (int i = 0; i < count; i++) shortScratch[i] = (short)Math.Clamp(scratch[i] * 32767f, -32768f, 32767f);
            Marshal.Copy(shortScratch, 0, data, count);
            return;
        }
        // 32-bit integer PCM.
        if (intScratch.Length < count) intScratch = new int[count];
        for (int i = 0; i < count; i++) intScratch[i] = (int)Math.Clamp(scratch[i] * 2147483647.0, int.MinValue, int.MaxValue);
        Marshal.Copy(intScratch, 0, data, count);
    }

    private void ReadFormat(IntPtr format)
    {
        ushort tag = (ushort)Marshal.ReadInt16(format, 0);
        Channels      = Marshal.ReadInt16(format, 2);
        SampleRate    = Marshal.ReadInt32(format, 4);
        bitsPerSample = Marshal.ReadInt16(format, 14);
        if (tag == FormatExtensible)
        {
            Guid subFormat = Marshal.PtrToStructure<Guid>(format + 24);
            isFloat = subFormat == SubtypeIeeeFloat;
        }
        else
        {
            isFloat = tag == FormatIeeeFloat;
        }
        if (!isFloat && bitsPerSample is not (16 or 32)) throw new NotSupportedException($"Output format {bitsPerSample}-bit integer is not supported");
    }

    /// <summary>Allocates a WAVEFORMATEXTENSIBLE for 32-bit float at the given rate and channel count; free with Marshal.FreeHGlobal.</summary>
    /// <param name="sampleRate">Frames per second.</param>
    /// <param name="channels">Interleaved channels.</param>
    /// <returns>The native format block.</returns>
    private static nint FloatFormat(int sampleRate, int channels)
    {
        const int size = 40;
        nint format = Marshal.AllocHGlobal(size);
        int blockAlign = channels * 4;
        Marshal.WriteInt16(format, 0,  unchecked((short)FormatExtensible));
        Marshal.WriteInt16(format, 2,  (short)channels);
        Marshal.WriteInt32(format, 4,  sampleRate);
        Marshal.WriteInt32(format, 8,  sampleRate * blockAlign);
        Marshal.WriteInt16(format, 12, (short)blockAlign);
        Marshal.WriteInt16(format, 14, 32);
        Marshal.WriteInt16(format, 16, 22);
        Marshal.WriteInt16(format, 18, 32);

        // Front left and right for stereo; other layouts are left for Windows to map.
        Marshal.WriteInt32(format, 20, channels == 2 ? 0x3 : 0);
        Marshal.StructureToPtr(SubtypeIeeeFloat, format + 24, false);
        return format;
    }

    /// <summary>Registers for default device changes once; Windows calls back on its own threads.</summary>
    private void Watch()
    {
        if (watcher is not null)
        {
            return;
        }

        watchEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        watcher         = new DefaultDeviceWatcher(this);
        watchEnumerator.RegisterEndpointNotificationCallback(watcher);
    }

    private void Unwatch()
    {
        if (watcher is null)
        {
            return;
        }

        watchEnumerator!.UnregisterEndpointNotificationCallback(watcher);
        Marshal.ReleaseComObject(watchEnumerator);
        watchEnumerator = null;
        watcher         = null;
    }

    private void Release()
    {
        if (render is not null) Marshal.ReleaseComObject(render);
        if (clock  is not null) Marshal.ReleaseComObject(clock);
        if (client is not null) Marshal.ReleaseComObject(client);
        render = null;
        clock  = null;
        client = null;
        wake?.Dispose();
        wake = null;
    }

    /// <summary>Stops following the default device and stops the render thread, which releases the device.</summary>
    public void Dispose()
    {
        lock (lifecycle)
        {
            wanted = false;
            Unwatch();
            Halt();
        }
    }

    /// <summary>Receives Windows' endpoint notifications and hands a default output change to the output.</summary>
    /// <param name="owner">The output to move.</param>
    private sealed class DefaultDeviceWatcher(WasapiOutput owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, uint newState)
        {
        }

        public void OnDeviceAdded(string deviceId)
        {
        }

        public void OnDeviceRemoved(string deviceId)
        {
        }

        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            // One call per role; the stream opens the multimedia default. A null id means no output device is left,
            // which the render thread reports itself. Audio calls are not allowed inside this callback, so queue it.
            if ((flow != EDataFlow.Render) || (role != ERole.Multimedia) || (defaultDeviceId is null))
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ => owner.SwitchDevice("default output changed"));
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
        }
    }

    // =========================================================================
    // COM
    // =========================================================================

    private const uint ClsCtxAll = 23;
    private const int  ShareModeShared = 0;
    private const int  StreamFlagsEventCallback = 0x00040000;
    private const int  StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
    private const int  StreamFlagsSrcDefaultQuality = 0x08000000;
    private const int  BufferFlagsSilent = 0x2;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IMMNotificationClient client);
        void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
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
