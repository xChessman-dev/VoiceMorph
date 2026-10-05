using NAudio.Effects;
using NAudio.Wave;
using VoiceMorph.App.Models;

namespace VoiceMorph.App.Services.Dsp;

/// <summary>
/// One consumer audio thread owns all effect state. UI changes publish an
/// immutable snapshot and are applied together at the next block boundary.
/// </summary>
public sealed class VoiceDspProcessor : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly GateEffect _gate = new();
    private readonly EqualizerBand[] _bands;
    private readonly Equalizer _equalizer;
    private readonly PitchShiftEffect _pitch = new() { FftSize = 1024, Oversampling = 4, Mix = 1 };
    // NAudio's SMB/Hann overlap-add has a 1.5x unity gain at oversampling=4.
    // Correct it before envelope analysis to avoid a level jump when enabling.
    private readonly GainEffect _pitchLevel = new() { LinearGain = 2f / 3f };
    private readonly FormantEnvelopeEffect _formant = new();
    private readonly CompressorEffect _compressor = new();
    private readonly DeEsserEffect _deEsser = new();
    private readonly SaturationEffect _saturation = new() { Curve = SaturationCurve.Tanh, OversampleFactor = 2 };
    private readonly DelayEffect _shadow = new() { Feedback = .025f, Damping = .72f };
    private readonly ReverbEffect _room = new() { Width = .25f };
    private readonly GainEffect _gain = new();
    private readonly LimiterEffect _limiter = new() { LookaheadMs = 2, ReleaseMs = 90, TruePeak = false };
    private readonly AudioEffect[] _effects;
    private readonly float[] _dryDelay;
    private float[] _dryBlock = new float[4096];
    private int _dryPosition;
    private float _wetMix = 1;
    private Snapshot _pending;
    private Snapshot? _applied;

    public VoiceDspProcessor(ISampleProvider source, VoiceSettings settings)
    {
        _source = source;
        _pending = new Snapshot(settings.Sanitize(), false);
        _bands =
        [
            new() { Type = EqualizerBandType.HighPass, Frequency = 65, Q = .707f },
            EqualizerBand.LowShelf(120, .75f, 0),
            EqualizerBand.Peaking(260, .8f, 0),
            EqualizerBand.Peaking(500, 1.1f, 0),
            EqualizerBand.Peaking(1_100, 1.2f, 0),
            EqualizerBand.Peaking(3_000, .85f, 0),
            EqualizerBand.HighShelf(7_500, .7f, 0),
            new() { Type = EqualizerBandType.LowPass, Frequency = 16_000, Q = .707f },
        ];
        _equalizer = new Equalizer(_bands);
        _effects = [_gate, _pitch, _pitchLevel, _formant, _equalizer, _deEsser, _compressor, _saturation, _shadow, _room, _gain];
        foreach (var effect in _effects) effect.Configure(WaveFormat);
        _limiter.Configure(WaveFormat);
        _dryDelay = new float[Math.Max(1, (_pitch.LatencySamples + _formant.LatencySamples) * WaveFormat.Channels)];
        ApplyPending();
    }

    public WaveFormat WaveFormat => _source.WaveFormat;
    public int LatencySamples => _pitch.LatencySamples + _formant.LatencySamples + _limiter.LatencySamples;
    public VoiceSettings AppliedSettings => _applied?.Settings ?? _pending.Settings;

    public void SetSettings(VoiceSettings settings)
    {
        var safe = settings.Sanitize();
        Snapshot current;
        do
        {
            current = Volatile.Read(ref _pending);
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref _pending,
                     new Snapshot(safe, current.Bypassed), current), current));
    }

    public void SetBypass(bool bypassed)
    {
        Snapshot current;
        do
        {
            current = Volatile.Read(ref _pending);
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref _pending,
                     new Snapshot(current.Settings, bypassed), current), current));
    }

    public int Read(Span<float> buffer)
    {
        ApplyPending();
        var count = _source.Read(buffer);
        var block = buffer[..count];
        if (_dryBlock.Length < count) _dryBlock = new float[count];
        for (var i = 0; i < count; i++)
        {
            var input = float.IsFinite(block[i]) ? Math.Clamp(block[i], -8, 8) : 0;
            block[i] = input;
            _dryBlock[i] = _dryDelay[_dryPosition];
            _dryDelay[_dryPosition] = input;
            if (++_dryPosition == _dryDelay.Length) _dryPosition = 0;
        }
        foreach (var effect in _effects) effect.Process(block);
        var target = _applied!.Bypassed || _applied.Settings.Strength <= .0001f ? 0f : 1f;
        var step = 1f / (WaveFormat.SampleRate * WaveFormat.Channels * .012f);
        for (var i = 0; i < count; i++)
        {
            _wetMix += Math.Clamp(target - _wetMix, -step, step);
            var wet = float.IsFinite(block[i]) ? block[i] : 0;
            block[i] = wet * _wetMix + _dryBlock[i] * (1 - _wetMix);
        }
        _limiter.Process(block);
        // Final finite/brick-wall guard also covers invalid endpoint samples and
        // the limiter's start-up transition. Never emit NaN or values above ceiling.
        var ceiling = MathF.Pow(10, _applied.Settings.LimiterCeilingDb / 20f);
        for (var i = 0; i < count; i++)
            block[i] = float.IsFinite(block[i]) ? Math.Clamp(block[i], -ceiling, ceiling) : 0;
        return count;
    }

    private void ApplyPending()
    {
        var snapshot = Volatile.Read(ref _pending);
        if (ReferenceEquals(snapshot, _applied)) return;
        _applied = snapshot;
        var s = snapshot.Settings.AtStrength(snapshot.Settings.Strength);
        _gate.ThresholdDb = s.GateThresholdDb;
        _gate.RangeDb = s.GateRangeDb;
        _gate.Ratio = 4;
        _gate.HysteresisDb = 5;
        _gate.AttackMs = s.GateAttackMs;
        _gate.HoldMs = 70;
        _gate.ReleaseMs = s.GateReleaseMs;
        _pitch.PitchSemitones = s.PitchSemitones;
        _formant.Semitones = s.FormantSemitones - s.PitchSemitones * s.FormantPreservation;
        _bands[0].Frequency = s.LowCutHz;
        _bands[1].GainDb = s.BassDb - s.Tone * 2.8f;
        _bands[2].GainDb = s.WarmthDb - s.Tone * 1.1f;
        _bands[3].GainDb = s.MudDb;
        _bands[4].GainDb = s.NasalDb;
        _bands[5].GainDb = s.PresenceDb + s.Tone * 1.8f;
        _bands[6].GainDb = s.AirDb + s.Tone * 2.4f;
        _bands[7].Frequency = Math.Min(s.HighCutHz, WaveFormat.SampleRate * .46f);
        _equalizer.Update();
        _deEsser.CrossoverFrequency = Math.Min(s.DeEssFrequencyHz, WaveFormat.SampleRate * .4f);
        _deEsser.ThresholdDb = -15 - s.DeEssAmount * 20;
        _deEsser.Ratio = 1 + s.DeEssAmount * 5;
        _deEsser.AttackMs = 1;
        _deEsser.ReleaseMs = 65;
        _compressor.ThresholdDb = -12 - s.Compression * 12;
        _compressor.Ratio = 1 + s.Compression * 3;
        _compressor.MakeUpGainDb = s.Compression * 1.5f;
        _compressor.KneeDb = s.CompressorKneeDb;
        _compressor.AttackMs = s.CompressorAttackMs;
        _compressor.ReleaseMs = s.CompressorReleaseMs;
        _saturation.DriveDb = s.DriveDb;
        _saturation.OutputGainDb = -s.DriveDb * .5f;
        _saturation.Mix = Math.Clamp(s.DriveDb / 5, 0, .6f);
        _shadow.DelayMs = s.ShadowDelayMs;
        _shadow.Mix = s.ShadowMix;
        _room.RoomSize = s.RoomSize;
        _room.Damping = s.RoomDamping;
        _room.Mix = s.RoomMix;
        _gain.GainDb = s.OutputGainDb;
        _limiter.CeilingDb = s.LimiterCeilingDb;
    }

    private sealed record Snapshot(VoiceSettings Settings, bool Bypassed);
}
