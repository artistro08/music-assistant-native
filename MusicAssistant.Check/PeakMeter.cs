using System.Runtime.InteropServices;

namespace MusicAssistant.Check;

/// <summary>
/// Reads the default render endpoint's peak meter for a few seconds, so a
/// playback test can prove sound is really leaving the device (screen and
/// speakers are not observable from a script). Core Audio IAudioMeterInformation.
/// </summary>
public static class PeakMeter
{
    public static float Sample(TimeSpan duration)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.GetDefaultAudioEndpoint(0, 1, out var device);
        var iid = typeof(IAudioMeterInformation).GUID;
        device.Activate(ref iid, 23, IntPtr.Zero, out var meterObject);
        var meter = (IAudioMeterInformation)meterObject;

        var peak = 0f;
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            meter.GetPeakValue(out var value);
            peak = Math.Max(peak, value);
            Thread.Sleep(20);
        }
        return peak;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
    }
}
