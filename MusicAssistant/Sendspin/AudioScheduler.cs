namespace MusicAssistant.Sendspin;

/// <summary>
/// Places decoded audio on the output timeline.
///
/// Chunks arrive with a server timestamp; the time filter turns that into the
/// local time the first sample must leave the device. The render callback
/// tells us the local time of the frame it is about to write, so each pass
/// compares the two and acts per the specification's playback rules: silence
/// until a chunk is due, drop what is already late, and in steady state
/// nudge by whole frames (never more than 0.5% of the frames) so the error
/// stays within a millisecond without audible artifacts. Snaps are reserved
/// for startup, seeks and errors beyond the soft range.
/// </summary>
/// <remarks>
/// @link https://github.com/Sendspin/spec/blob/main/roles/player/v1.md#playback-synchronization
/// </remarks>
public sealed class AudioScheduler
{
    public sealed record Chunk(float[] Samples, int Frames, long ServerTimeUs, int Generation);

    private const long  SoftDeadbandUs   = 100;     // below this, leave the audio alone
    private const int   CorrectionSpacing = 200;    // frames between one-frame corrections: 0.5% max speed change
    private const float GainTimeConstantMs = 15f;

    // Beyond this the scheduler resyncs in one shot. Small on the LAN where timing is exact; wide over the
    // relay, where a tighter bound would hard-snap on ordinary network jitter and glitch continuously.
    private readonly long snapThresholdUs;

    private readonly TimeFilter timeFilter;
    private readonly object gate = new();
    private readonly PriorityQueue<Chunk, long> queue = new();

    private Chunk? current;
    private int    offset;              // frames already consumed from `current`
    private int    framesSinceCorrection;
    private int    generation;
    private float  gain = 1f;
    private float  targetGain = 1f;
    private long   syncErrorUs;
    private int    droppedLate;
    private int    resyncs;

    public int SampleRate { get; }
    public int Channels   { get; }

    public AudioScheduler(TimeFilter timeFilter, int sampleRate, int channels, bool remote)
    {
        this.timeFilter = timeFilter;
        SampleRate = sampleRate;
        Channels   = channels;
        snapThresholdUs = remote ? 60_000 : 1_500;
    }

    /// <summary>Extra local delay applied to every chunk (output_delay / static delay), microseconds.</summary>
    public long OutputDelayUs { get; set; }

    /// <summary>Last measured error between the output timeline and the target, microseconds (positive = late).</summary>
    public long SyncErrorUs => Interlocked.Read(ref syncErrorUs);
    public int  Resyncs     => resyncs;
    public int  DroppedLateChunks => droppedLate;
    public bool HasAudio { get { lock (gate) return current is not null || queue.Count > 0; } }

    public void SetGain(float linear) => targetGain = Math.Clamp(linear, 0f, 1f);

    public void Enqueue(Chunk chunk)
    {
        lock (gate)
        {
            if (chunk.Generation != generation) return;
            queue.Enqueue(chunk, chunk.ServerTimeUs);
        }
    }

    /// <summary>Drop everything (stream/clear, stream/end); chunks from before this call are ignored when they arrive late.</summary>
    public void Clear()
    {
        lock (gate)
        {
            generation++;
            queue.Clear();
            current = null;
            offset  = 0;
            framesSinceCorrection = 0;
        }
    }

    public int CurrentGeneration { get { lock (gate) return generation; } }

    /// <summary>Render callback: fill `frames` frames whose first frame plays at `firstFrameTimeUs`.</summary>
    public void Render(Span<float> buffer, int frames, long firstFrameTimeUs)
    {
        var written = 0;
        var frameUs = 1_000_000.0 / SampleRate;

        lock (gate)
        {
            if (!timeFilter.IsSynchronized) { ApplyGain(buffer, frames); return; }

            while (written < frames)
            {
                if (current is null)
                {
                    if (!queue.TryDequeue(out current, out _)) break;
                    offset = 0;
                }

                var slotTime   = firstFrameTimeUs + (long)(written * frameUs);
                var targetTime = timeFilter.ComputeClientTime(current.ServerTimeUs) + OutputDelayUs + (long)(offset * frameUs);
                var error      = slotTime - targetTime;   // > 0: this sample is overdue
                Interlocked.Exchange(ref syncErrorUs, error);

                if (error > snapThresholdUs)
                {
                    // Late: skip what has already passed (one-shot resync)
                    var skip = (int)(error / frameUs);
                    resyncs++;
                    if (offset + skip >= current.Frames) { droppedLate++; current = null; continue; }
                    offset += skip;
                    continue;
                }
                if (error < -snapThresholdUs)
                {
                    // Early: silence until the chunk is due
                    var wait = (int)Math.Min(frames - written, (-error) / frameUs);
                    if (wait <= 0) wait = 1;
                    written += wait;   // buffer is pre-cleared
                    continue;
                }

                // Steady state: whole-frame nudges, spaced so the speed change stays under 0.5%
                if (Math.Abs(error) > SoftDeadbandUs && framesSinceCorrection >= CorrectionSpacing)
                {
                    framesSinceCorrection = 0;
                    if (error > 0)
                    {
                        offset++;   // running late: drop one frame
                        if (offset >= current.Frames) { current = null; continue; }
                    }
                    else
                    {
                        // running early: repeat one frame
                        current.Samples.AsSpan(offset * Channels, Channels).CopyTo(buffer.Slice(written * Channels, Channels));
                        written++;
                        if (written >= frames) break;
                    }
                }

                var take = Math.Min(frames - written, current.Frames - offset);
                current.Samples.AsSpan(offset * Channels, take * Channels).CopyTo(buffer.Slice(written * Channels, take * Channels));
                written += take;
                offset  += take;
                framesSinceCorrection += take;
                if (offset >= current.Frames) current = null;
            }
        }

        ApplyGain(buffer, frames);
    }

    private float peak;

    /// <summary>Largest sample magnitude rendered since the last read (diagnostics).</summary>
    public float TakePeak() => Interlocked.Exchange(ref peak, 0f);

    /// <summary>Volume ramps toward its target with a short time constant so changes never click.</summary>
    private void ApplyGain(Span<float> buffer, int frames)
    {
        var alpha = 1f - MathF.Exp(-1000f / (GainTimeConstantMs * SampleRate));
        var max   = 0f;
        for (var f = 0; f < frames; f++)
        {
            gain += (targetGain - gain) * alpha;
            var g = gain;
            var row = buffer.Slice(f * Channels, Channels);
            for (var c = 0; c < Channels; c++)
            {
                row[c] *= g;
                var magnitude = MathF.Abs(row[c]);
                if (magnitude > max) max = magnitude;
            }
        }
        if (max > peak) peak = max;
    }
}

