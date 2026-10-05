namespace VoiceMorph.App.Models;

public sealed record VoiceSettings(
    float PitchSemitones,
    float Tone,
    float DriveDb,
    float GateThresholdDb,
    float Compression,
    float ShadowMix,
    float RoomMix)
{
    public float Strength { get; init; } = 1f;
    public float FormantSemitones { get; init; }
    public float FormantPreservation { get; init; } = 1f;
    public float LowCutHz { get; init; } = 65f;
    public float HighCutHz { get; init; } = 16_000f;
    public float BassDb { get; init; }
    public float WarmthDb { get; init; }
    public float MudDb { get; init; }
    public float NasalDb { get; init; }
    public float PresenceDb { get; init; }
    public float AirDb { get; init; }
    public float DeEssAmount { get; init; } = 0.18f;
    public float DeEssFrequencyHz { get; init; } = 6_000f;
    public float CompressorAttackMs { get; init; } = 12f;
    public float CompressorReleaseMs { get; init; } = 140f;
    public float CompressorKneeDb { get; init; } = 6f;
    public float GateAttackMs { get; init; } = 3f;
    public float GateReleaseMs { get; init; } = 160f;
    public float GateRangeDb { get; init; } = -24f;
    public float OutputGainDb { get; init; }
    public float ShadowDelayMs { get; init; } = 20f;
    public float RoomSize { get; init; } = 0.16f;
    public float RoomDamping { get; init; } = 0.8f;
    public float LimiterCeilingDb { get; init; } = -1f;

    public static VoiceSettings Neutral { get; } = new(0, 0, 0, -55, 0, 0, 0)
    {
        LowCutHz = 35f,
        DeEssAmount = 0f,
    };

    public static VoiceSettings FromPreset(VoicePreset preset) => new(
        preset.PitchSemitones,
        preset.Tone,
        preset.DriveDb,
        preset.GateThresholdDb,
        preset.Compression,
        preset.ShadowMix,
        preset.RoomMix)
    {
        // Small independent envelope changes sound more natural than shifting
        // both vocal pitch and vocal-tract resonances by the same amount.
        FormantSemitones = preset.Id switch
        {
            "arbiter" => -0.55f,
            "velvet" => -0.8f,
            "entity" => -0.35f,
            "oracle" => 0.2f,
            _ => -0.15f,
        },
        WarmthDb = preset.Id is "arbiter" or "velvet" ? 0.6f : 0,
        MudDb = -0.8f,
        NasalDb = -0.3f,
        DeEssAmount = 0.2f,
    };

    public VoiceSettings Sanitize()
    {
        var sanitized = this;
        foreach (var parameter in VoiceParameter.All)
        {
            var value = parameter.Get(sanitized);
            if (!float.IsFinite(value)) value = parameter.Get(Neutral);
            sanitized = parameter.SetUnchecked(sanitized, Math.Clamp(value, parameter.Min, parameter.Max));
        }
        return sanitized;
    }

    /// <summary>Scales the vocal transformation while retaining safety and timing controls.</summary>
    public VoiceSettings AtStrength(float strength)
    {
        var s = Sanitize();
        var amount = float.IsFinite(strength) ? Math.Clamp(strength, 0f, 1f) : 1f;
        return s with
        {
            Strength = amount,
            PitchSemitones = s.PitchSemitones * amount,
            FormantSemitones = s.FormantSemitones * amount,
            Tone = s.Tone * amount,
            DriveDb = s.DriveDb * amount,
            Compression = s.Compression * amount,
            ShadowMix = s.ShadowMix * amount,
            RoomMix = s.RoomMix * amount,
            BassDb = s.BassDb * amount,
            WarmthDb = s.WarmthDb * amount,
            MudDb = s.MudDb * amount,
            NasalDb = s.NasalDb * amount,
            PresenceDb = s.PresenceDb * amount,
            AirDb = s.AirDb * amount,
            DeEssAmount = s.DeEssAmount * amount,
            OutputGainDb = s.OutputGainDb * amount,
        };
    }
}
