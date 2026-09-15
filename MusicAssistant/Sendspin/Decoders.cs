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
    /// <summary>The audio format a stream/start message announces.</summary>
    /// <param name="Codec">The codec name: "pcm" or "opus".</param>
    /// <param name="SampleRate">The stream's sample rate in hertz.</param>
    /// <param name="Channels">The stream's channel count.</param>
    /// <param name="BitDepth">Bits per PCM sample: 16, 24 or 32.</param>
    public sealed record Format(string Codec, int SampleRate, int Channels, int BitDepth);

    private readonly int outputRate;
    private readonly int outputChannels;
    private Format?       format;
    private IOpusDecoder? opus;
    private short[]       opusBuffer = new short[5760 * 2];

    /// <summary>Fractional frame position carried across chunks.</summary>
    private double        resamplePosition;

    /// <summary>Last input frame, for interpolation continuity.</summary>
    private float[]       resampleTail = [];

    /// <summary>Creates a decoder that produces frames in the output device's format.</summary>
    /// <param name="outputRate">The output device's sample rate.</param>
    /// <param name="outputChannels">The output device's channel count.</param>
    public ChunkDecoder(int outputRate, int outputChannels)
    {
        this.outputRate     = outputRate;
        this.outputChannels = outputChannels;
    }

    /// <summary>Switches to a new stream format, creating an Opus decoder when the codec needs one.</summary>
    /// <param name="streamFormat">The format from stream/start.</param>
    public void Configure(Format streamFormat)
    {
        format = streamFormat;
        opus?.Dispose();
        opus = null;
        resamplePosition = 0;
        resampleTail = [];
        if (streamFormat.Codec == "opus") opus = OpusCodecFactory.CreateDecoder(streamFormat.SampleRate, streamFormat.Channels);
    }

    /// <summary>Whether a stream format is set, so the next stream/start is a format update.</summary>
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
    /// <param name="payload">The encoded audio bytes.</param>
    /// <returns>Interleaved samples in the output format and their frame count, or <see langword="null"/>.</returns>
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
                for (int i = 0; i < native.Length; i++) native[i] = opusBuffer[i] / 32768f;
                break;
            default:
                return null;
        }

        float[] mapped = MapChannels(native, frames, format.Channels);
        return format.SampleRate == outputRate ? (mapped, frames) : Resample(mapped, frames, format.SampleRate);
    }

    private static (float[] Samples, int Frames) DecodePcm(ReadOnlySpan<byte> data, Format format)
    {
        int bytesPerSample = format.BitDepth / 8;
        int count   = data.Length / bytesPerSample;
        float[] samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> at = data.Slice(i * bytesPerSample, bytesPerSample);
            samples[i] = format.BitDepth switch
            {
                16 => BinaryPrimitives.ReadInt16LittleEndian(at) / 32768f,
                // Sign-extend the 24-bit value.
                24 => ((at[0] | (at[1] << 8) | (at[2] << 16)) << 8 >> 8) / 8388608f,
                32 => BinaryPrimitives.ReadInt32LittleEndian(at) / 2147483648f,
                _  => 0f,
            };
        }
        return (samples, count / format.Channels);
    }

    private float[] MapChannels(float[] samples, int frames, int channels)
    {
        if (channels == outputChannels) return samples;
        float[] output = new float[frames * outputChannels];
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < outputChannels; c++)
            {
                output[f * outputChannels + c] = channels == 1 ? samples[f] : c < channels ? samples[f * channels + c] : 0f;
            }
        }
        return output;
    }

    /// <summary>Linear interpolation from the stream rate to the device rate, continuous across chunk boundaries.</summary>
    private (float[] Samples, int Frames) Resample(float[] input, int frames, int inputRate)
    {
        double ratio  = (double)inputRate / outputRate;
        float[] source = resampleTail.Length == 0 ? input : [.. resampleTail, .. input];
        int sourceFrames = source.Length / outputChannels;
        int outFrames = (int)Math.Floor((sourceFrames - 1 - resamplePosition) / ratio) + 1;
        if (outFrames <= 0)
        {
            resampleTail = source;
            return ([], 0);
        }

        float[] output = new float[outFrames * outputChannels];
        double position = resamplePosition;
        for (int f = 0; f < outFrames; f++, position += ratio)
        {
            int index = (int)position;
            float frac  = (float)(position - index);
            for (int c = 0; c < outputChannels; c++)
            {
                float a = source[index * outputChannels + c];
                float b = source[Math.Min(index + 1, sourceFrames - 1) * outputChannels + c];
                output[f * outputChannels + c] = a + (b - a) * frac;
            }
        }
        // Keep the last frame consumed so the next chunk interpolates from it. The clamped index must also
        // anchor resamplePosition, or the fractional offset drifts one frame from where the tail begins.
        int consumed = Math.Min((int)position, sourceFrames - 1);
        resamplePosition = position - consumed;
        resampleTail = source.AsSpan(consumed * outputChannels).ToArray();
        return (output, outFrames);
    }

    /// <summary>Releases the Opus decoder, if one is open.</summary>
    public void Dispose()
    {
        opus?.Dispose();
        opus = null;
    }
}
