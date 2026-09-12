using System.Buffers.Binary;
using Concentus;

namespace MusicAssistant.Sendspin;

/// <summary>
/// Turns Sendspin audio chunks into interleaved float frames in the output
/// device's format. PCM is unpacked directly; Opus goes through Concentus.
/// Channel count and sample rate are adapted to the device (mono is spread to
/// every channel; a rate mismatch is bridged by linear interpolation, which
/// only happens for Opus on a device that is not running at 48 kHz).
/// </summary>
/// <remarks>
/// @link https://github.com/Sendspin/spec/blob/main/roles/player/v1.md#server--client-audio-chunks-binary
/// </remarks>
public sealed class ChunkDecoder
{
    public sealed record Format(string Codec, int SampleRate, int Channels, int BitDepth);

    private readonly int outputRate;
    private readonly int outputChannels;
    private Format?       format;
    private IOpusDecoder? opus;
    private short[]       opusBuffer = new short[5760 * 2];
    private double        resamplePosition;   // fractional frame position carried across chunks
    private float[]       resampleTail = [];  // last input frame, for interpolation continuity

    public ChunkDecoder(int outputRate, int outputChannels)
    {
        this.outputRate     = outputRate;
        this.outputChannels = outputChannels;
    }

    public void Configure(Format streamFormat)
    {
        format = streamFormat;
        opus?.Dispose();
        opus = null;
        resamplePosition = 0;
        resampleTail = [];
        if (streamFormat.Codec == "opus") opus = OpusCodecFactory.CreateDecoder(streamFormat.SampleRate, streamFormat.Channels);
    }

    public bool IsConfigured => format is not null;

    /// <summary>Forget the stream format (stream/end); the next stream/start is a fresh start, not a format update.</summary>
    public void Reset()
    {
        format = null;
        opus?.Dispose();
        opus = null;
        resamplePosition = 0;
        resampleTail = [];
    }

    /// <summary>Decode the payload of a type-4 audio message (bytes after the 13-byte header). Null when the codec is not handled.</summary>
    public (float[] Samples, int Frames)? Decode(ReadOnlySpan<byte> payload)
    {
        if (format is null) return null;
        float[] native;
        int frames;

        switch (format.Codec)
        {
            case "pcm":
                (native, frames) = DecodePcm(payload, format);
                break;
            case "opus":
                if (opus is null) return null;
                frames = opus.Decode(payload, opusBuffer, opusBuffer.Length / format.Channels, false);
                native = new float[frames * format.Channels];
                for (var i = 0; i < native.Length; i++) native[i] = opusBuffer[i] / 32768f;
                break;
            default:
                return null;
        }

        var mapped = MapChannels(native, frames, format.Channels);
        return format.SampleRate == outputRate ? (mapped, frames) : Resample(mapped, frames, format.SampleRate);
    }

    private static (float[] Samples, int Frames) DecodePcm(ReadOnlySpan<byte> data, Format format)
    {
        var bytesPerSample = format.BitDepth / 8;
        var count   = data.Length / bytesPerSample;
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            var at = data.Slice(i * bytesPerSample, bytesPerSample);
            samples[i] = format.BitDepth switch
            {
                16 => BinaryPrimitives.ReadInt16LittleEndian(at) / 32768f,
                24 => ((at[0] | (at[1] << 8) | (at[2] << 16)) << 8 >> 8) / 8388608f,   // sign-extend the 24-bit value
                32 => BinaryPrimitives.ReadInt32LittleEndian(at) / 2147483648f,
                _  => 0f,
            };
        }
        return (samples, count / format.Channels);
    }

    private float[] MapChannels(float[] samples, int frames, int channels)
    {
        if (channels == outputChannels) return samples;
        var output = new float[frames * outputChannels];
        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < outputChannels; c++)
            {
                output[f * outputChannels + c] = channels == 1 ? samples[f] : c < channels ? samples[f * channels + c] : 0f;
            }
        }
        return output;
    }

    /// <summary>Linear interpolation from the stream rate to the device rate, continuous across chunk boundaries.</summary>
    private (float[] Samples, int Frames) Resample(float[] input, int frames, int inputRate)
    {
        var ratio  = (double)inputRate / outputRate;
        var source = resampleTail.Length == 0 ? input : [.. resampleTail, .. input];
        var sourceFrames = source.Length / outputChannels;
        var outFrames = (int)Math.Floor((sourceFrames - 1 - resamplePosition) / ratio) + 1;
        if (outFrames <= 0) { resampleTail = source; return ([], 0); }

        var output = new float[outFrames * outputChannels];
        var position = resamplePosition;
        for (var f = 0; f < outFrames; f++, position += ratio)
        {
            var index = (int)position;
            var frac  = (float)(position - index);
            for (var c = 0; c < outputChannels; c++)
            {
                var a = source[index * outputChannels + c];
                var b = source[Math.Min(index + 1, sourceFrames - 1) * outputChannels + c];
                output[f * outputChannels + c] = a + (b - a) * frac;
            }
        }
        // Keep the last frame consumed so the next chunk interpolates from it. The clamped index must also
        // anchor resamplePosition, or the fractional offset drifts one frame from where the tail begins.
        var consumed = Math.Min((int)position, sourceFrames - 1);
        resamplePosition = position - consumed;
        resampleTail = source.AsSpan(consumed * outputChannels).ToArray();
        return (output, outFrames);
    }

    public void Dispose()
    {
        opus?.Dispose();
        opus = null;
    }
}
