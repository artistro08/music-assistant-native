using System.Diagnostics;

namespace MusicAssistant.Sendspin;

/// <summary>Monotonic microsecond clock on QueryPerformanceCounter, the same time base WASAPI stamps audio positions with.</summary>
public static class Clock
{
    private static readonly long Frequency = Stopwatch.Frequency;

    public static long NowUs() => TicksToUs(Stopwatch.GetTimestamp());

    /// <summary>Integer arithmetic so the value stays exact for years of uptime.</summary>
    public static long TicksToUs(long ticks)
    {
        var seconds   = ticks / Frequency;
        var remainder = ticks % Frequency;
        return seconds * 1_000_000 + remainder * 1_000_000 / Frequency;
    }
}
