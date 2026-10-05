using NAudio.Dsp;
using NAudio.Effects;
using NAudio.Wave;

namespace VoiceMorph.App.Services.Dsp;

/// <summary>
/// Shifts the smoothed spectral envelope without moving harmonic frequencies.
/// This is an approximate formant correction, not a speaker identity model.
/// A 1024-sample STFT keeps processing inexpensive and latency bounded.
/// </summary>
public sealed class FormantEnvelopeEffect : AudioEffect
{
    private const int Size = 1024;
    private const int Hop = 256;
    private const int Half = Size / 2;
    private readonly float[] _window = Enumerable.Range(0, Size)
        .Select(i => .5f - .5f * MathF.Cos(2 * MathF.PI * i / Size)).ToArray();
    private ChannelState[] _states = [];
    private float _semitones;

    public float Semitones
    {
        get => _semitones;
        set => _semitones = float.IsFinite(value) ? Math.Clamp(value, -7f, 7f) : 0f;
    }

    // The input FIFO offset is Size-Hop, but block scheduling contributes one
    // additional hop. Impulse measurements therefore give a full frame delay.
    public override int LatencySamples => Size;

    protected override void OnConfigure(WaveFormat format) =>
        _states = Enumerable.Range(0, format.Channels).Select(_ => new ChannelState()).ToArray();

    protected override void ProcessBlock(Span<float> buffer)
    {
        var ratio = MathF.Pow(2, Semitones / 12f);
        for (var frame = 0; frame < buffer.Length; frame += Channels)
        {
            for (var channel = 0; channel < Channels && frame + channel < buffer.Length; channel++)
            {
                var state = _states[channel];
                state.Input[state.Rover] = buffer[frame + channel];
                buffer[frame + channel] = state.Output[state.Rover - (Size - Hop)];
                if (++state.Rover < Size) continue;
                state.Rover = Size - Hop;
                Transform(state, ratio);
            }
        }
    }

    public override void Reset()
    {
        foreach (var state in _states) state.Reset();
        base.Reset();
    }

    private void Transform(ChannelState state, float ratio)
    {
        for (var i = 0; i < Size; i++)
        {
            state.Spectrum[i].X = state.Input[i] * _window[i];
            state.Spectrum[i].Y = 0;
        }
        FastFourierTransform.FFT(true, 10, state.Spectrum);
        if (Math.Abs(ratio - 1f) > .0001f)
        {
            for (var k = 0; k <= Half; k++)
            {
                var c = state.Spectrum[k];
                state.LogEnvelope[k] = MathF.Log(MathF.Max(1e-7f, MathF.Sqrt(c.X * c.X + c.Y * c.Y)));
            }
            // Three box smooths approximate a broad envelope, suppressing the
            // harmonic comb so pitch is not moved by the envelope transform.
            for (var pass = 0; pass < 3; pass++)
            {
                for (var k = 0; k <= Half; k++)
                {
                    var sum = 0f;
                    var start = Math.Max(0, k - 4);
                    var end = Math.Min(Half, k + 4);
                    for (var j = start; j <= end; j++) sum += state.LogEnvelope[j];
                    state.Smooth[k] = sum / (end - start + 1);
                }
                Array.Copy(state.Smooth, state.LogEnvelope, Half + 1);
            }
            for (var k = 1; k < Half; k++)
            {
                var source = Math.Clamp(k / ratio, 0, Half - 1f);
                var bin = (int)source;
                var envelope = state.LogEnvelope[bin] +
                    (state.LogEnvelope[bin + 1] - state.LogEnvelope[bin]) * (source - bin);
                // Limit envelope boost/cut to avoid amplifying numerical noise
                // and to retain consonants when a target transform is extreme.
                var gain = MathF.Exp(Math.Clamp(envelope - state.LogEnvelope[k], -1.1f, .8f));
                state.Spectrum[k].X *= gain;
                state.Spectrum[k].Y *= gain;
                state.Spectrum[Size - k].X *= gain;
                state.Spectrum[Size - k].Y *= gain;
            }
        }
        FastFourierTransform.FFT(false, 10, state.Spectrum);
        for (var i = 0; i < Size; i++)
            state.Accumulator[i] += state.Spectrum[i].X * _window[i] * (2f / 3f);
        Array.Copy(state.Accumulator, state.Output, Hop);
        Array.Copy(state.Accumulator, Hop, state.Accumulator, 0, Size - Hop);
        Array.Clear(state.Accumulator, Size - Hop, Hop);
        Array.Copy(state.Input, Hop, state.Input, 0, Size - Hop);
    }

    private sealed class ChannelState
    {
        public float[] Input { get; } = new float[Size];
        public float[] Output { get; } = new float[Hop];
        public float[] Accumulator { get; } = new float[Size];
        public Complex[] Spectrum { get; } = new Complex[Size];
        public float[] LogEnvelope { get; } = new float[Half + 1];
        public float[] Smooth { get; } = new float[Half + 1];
        public int Rover { get; set; } = Size - Hop;

        public void Reset()
        {
            Array.Clear(Input); Array.Clear(Output); Array.Clear(Accumulator);
            Array.Clear(Spectrum); Array.Clear(LogEnvelope); Array.Clear(Smooth);
            Rover = Size - Hop;
        }
    }
}
