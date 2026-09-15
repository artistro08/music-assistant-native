using MusicAssistant.Sendspin;
using Xunit;

namespace MusicAssistant.Tests;

/// <summary>
/// The scheduler decides when each chunk leaves the speaker, which can't be judged by ear. These check the rules
/// from the player specification: silence before a chunk is due, the samples at its time, and late audio dropped.
/// </summary>
public sealed class AudioSchedulerTests
{
    private const int SampleRate = 48000;
    private const int Channels   = 2;

    [Fact]
    public void Renders_silence_before_the_clock_is_synchronized()
    {
        var scheduler = new AudioScheduler(new TimeFilter(), SampleRate, Channels, remote: false);
        scheduler.Enqueue(Chunk(scheduler, 1_000_000, 480, 0.5f));

        Assert.True(IsSilent(Render(scheduler, 480, 1_000_000)));
    }

    [Fact]
    public void Plays_a_chunk_at_its_server_time()
    {
        AudioScheduler scheduler = Synchronized();
        long due = Clock.NowUs() + 100_000;
        scheduler.Enqueue(Chunk(scheduler, due, 480, 0.5f));

        float[] early = Render(scheduler, 480, due - 500_000);
        float[] onTime = Render(scheduler, 480, due);

        Assert.True(IsSilent(early));
        Assert.False(IsSilent(onTime));
        Assert.True(scheduler.SyncErrorUs is > -2000 and < 2000);
    }

    [Fact]
    public void Drops_a_chunk_that_is_already_past()
    {
        AudioScheduler scheduler = Synchronized();
        long due = Clock.NowUs();
        scheduler.Enqueue(Chunk(scheduler, due, 480, 0.5f));

        // A whole second late: every sample in the chunk is past due, so none of it may play.
        float[] rendered = Render(scheduler, 480, due + 1_000_000);

        Assert.True(IsSilent(rendered));
        Assert.Equal(1, scheduler.DroppedLateChunks);
        Assert.False(scheduler.HasAudio);
    }

    [Fact]
    public void Clear_throws_away_queued_audio()
    {
        AudioScheduler scheduler = Synchronized();
        long due = Clock.NowUs() + 100_000;
        scheduler.Enqueue(Chunk(scheduler, due, 480, 0.5f));
        scheduler.Clear();

        Assert.False(scheduler.HasAudio);
        Assert.True(IsSilent(Render(scheduler, 480, due)));
    }

    [Fact]
    public void Ignores_a_chunk_decoded_before_the_last_clear()
    {
        AudioScheduler scheduler = Synchronized();
        long due = Clock.NowUs() + 100_000;
        AudioScheduler.Chunk stale = Chunk(scheduler, due, 480, 0.5f);
        scheduler.Clear();
        scheduler.Enqueue(stale);

        Assert.False(scheduler.HasAudio);
    }

    [Fact]
    public void Mute_ramps_down_to_silence()
    {
        AudioScheduler scheduler = Synchronized();
        long due = Clock.NowUs() + 100_000;

        // 200 ms of audio: the gain ramps toward its target over 15 ms, so the tail is where mute has taken hold.
        const int frames = SampleRate / 5;
        scheduler.Enqueue(Chunk(scheduler, due, frames, 0.5f));
        scheduler.SetGain(0f);
        float[] rendered = Render(scheduler, frames, due);

        float head = Loudest(rendered, 0, SampleRate / 1000);
        float tail = Loudest(rendered, frames - (SampleRate / 100), frames);

        Assert.True(head > 0.1f, $"expected audio before the ramp, saw {head}");
        Assert.True(tail < 0.001f, $"expected silence after the ramp, saw {tail}");
    }

    /// <summary>A scheduler whose clock filter is synchronized with no offset, so server time equals local time.</summary>
    private static AudioScheduler Synchronized()
    {
        var filter = new TimeFilter();
        long now = Clock.NowUs();
        filter.Update(0, 1000, now);
        filter.Update(0, 1000, now + 1000);
        Assert.True(filter.IsSynchronized);
        return new AudioScheduler(filter, SampleRate, Channels, remote: false);
    }

    private static AudioScheduler.Chunk Chunk(AudioScheduler scheduler, long serverTimeUs, int frames, float level)
    {
        float[] samples = new float[frames * Channels];
        Array.Fill(samples, level);
        return new AudioScheduler.Chunk(samples, frames, serverTimeUs, scheduler.CurrentGeneration);
    }

    private static float[] Render(AudioScheduler scheduler, int frames, long firstFrameTimeUs)
    {
        float[] buffer = new float[frames * Channels];
        scheduler.Render(buffer, frames, firstFrameTimeUs);
        return buffer;
    }

    private static bool IsSilent(float[] buffer) => buffer.All(sample => Math.Abs(sample) < 0.0001f);

    /// <summary>The loudest sample between two frame positions.</summary>
    /// <param name="buffer">The rendered interleaved buffer.</param>
    /// <param name="fromFrame">First frame to look at.</param>
    /// <param name="toFrame">Frame to stop before.</param>
    /// <returns>The largest sample magnitude in that range.</returns>
    private static float Loudest(float[] buffer, int fromFrame, int toFrame)
        => buffer.Skip(fromFrame * Channels).Take((toFrame - fromFrame) * Channels).Max(Math.Abs);
}
