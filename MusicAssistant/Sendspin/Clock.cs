using System.Diagnostics;

namespace MusicAssistant.Sendspin;

/// <summary>Monotonic microsecond clock on QueryPerformanceCounter, the same time base WASAPI stamps audio positions with.</summary>
public static class Clock
{
    private static readonly long Frequency = Stopwatch.Frequency;

    /// <summary>Current local time in microseconds on the QueryPerformanceCounter time base.</summary>
    /// <returns>The current time in microseconds.</returns>
    public static long NowUs() => TicksToUs(Stopwatch.GetTimestamp());

    /// <summary>Integer arithmetic so the value stays exact for years of uptime.</summary>
    /// <param name="ticks">A <see cref="Stopwatch"/> timestamp in performance counter ticks.</param>
    /// <returns>The same instant in microseconds.</returns>
    public static long TicksToUs(long ticks)
    {
        long seconds   = ticks / Frequency;
        long remainder = ticks % Frequency;
        return seconds * 1_000_000 + remainder * 1_000_000 / Frequency;
    }
}
