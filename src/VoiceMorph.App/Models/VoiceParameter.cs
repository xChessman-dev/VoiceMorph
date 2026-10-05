namespace VoiceMorph.App.Models;

/// <summary>Single source of truth for slider limits, profile validation and DSP settings.</summary>
public sealed record VoiceParameter(
    string Key, string Name, string Group, float Min, float Max, float Step, string Unit,
    Func<VoiceSettings, float> Getter, Func<VoiceSettings, float, VoiceSettings> Setter)
{
    public float Get(VoiceSettings settings) => Getter(settings);

    public VoiceSettings Set(VoiceSettings settings, float value) =>
        Setter(settings, float.IsFinite(value) ? Math.Clamp(value, Min, Max) : Getter(VoiceSettings.Neutral));

    internal VoiceSettings SetUnchecked(VoiceSettings settings, float value) => Setter(settings, value);

    public static IReadOnlyList<VoiceParameter> All { get; } =
    [
        new("Strength", "Сила преобразования", "Характер", 0, 1, .01f, "%", s => s.Strength, (s,v) => s with { Strength = v }),
        new("PitchSemitones", "Высота", "Характер", -4, 3, .05f, "st", s => s.PitchSemitones, (s,v) => s with { PitchSemitones = v }),
        new("FormantSemitones", "Резонансы голоса", "Характер", -3, 3, .05f, "st", s => s.FormantSemitones, (s,v) => s with { FormantSemitones = v }),
        new("FormantPreservation", "Сохранение формант", "Характер", 0, 1, .01f, "%", s => s.FormantPreservation, (s,v) => s with { FormantPreservation = v }),
        new("Tone", "Спектральный оттенок", "Характер", -1, 1, .02f, "", s => s.Tone, (s,v) => s with { Tone = v }),
        new("DriveDb", "Плотность гармоник", "Характер", 0, 5, .1f, "dB", s => s.DriveDb, (s,v) => s with { DriveDb = v }),
        new("LowCutHz", "Срез гула", "Тембр", 30, 180, 5, "Hz", s => s.LowCutHz, (s,v) => s with { LowCutHz = v }),
        new("HighCutHz", "Верхняя граница", "Тембр", 6_000, 20_000, 250, "Hz", s => s.HighCutHz, (s,v) => s with { HighCutHz = v }),
        new("BassDb", "Низкий резонанс", "Тембр", -6, 6, .1f, "dB", s => s.BassDb, (s,v) => s with { BassDb = v }),
        new("WarmthDb", "Теплота", "Тембр", -5, 5, .1f, "dB", s => s.WarmthDb, (s,v) => s with { WarmthDb = v }),
        new("MudDb", "Мутность", "Тембр", -6, 4, .1f, "dB", s => s.MudDb, (s,v) => s with { MudDb = v }),
        new("NasalDb", "Носовой оттенок", "Тембр", -5, 4, .1f, "dB", s => s.NasalDb, (s,v) => s with { NasalDb = v }),
        new("PresenceDb", "Ясность согласных", "Тембр", -5, 5, .1f, "dB", s => s.PresenceDb, (s,v) => s with { PresenceDb = v }),
        new("AirDb", "Воздушность", "Тембр", -6, 5, .1f, "dB", s => s.AirDb, (s,v) => s with { AirDb = v }),
        new("Compression", "Выравнивание громкости", "Динамика", 0, 1, .01f, "%", s => s.Compression, (s,v) => s with { Compression = v }),
        new("CompressorAttackMs", "Атака компрессора", "Динамика", 2, 60, 1, "ms", s => s.CompressorAttackMs, (s,v) => s with { CompressorAttackMs = v }),
        new("CompressorReleaseMs", "Восстановление компрессора", "Динамика", 50, 500, 5, "ms", s => s.CompressorReleaseMs, (s,v) => s with { CompressorReleaseMs = v }),
        new("CompressorKneeDb", "Мягкость компрессии", "Динамика", 0, 12, .5f, "dB", s => s.CompressorKneeDb, (s,v) => s with { CompressorKneeDb = v }),
        new("DeEssAmount", "Смягчение свистящих", "Чистота", 0, 1, .01f, "%", s => s.DeEssAmount, (s,v) => s with { DeEssAmount = v }),
        new("DeEssFrequencyHz", "Диапазон свистящих", "Чистота", 3_500, 9_000, 100, "Hz", s => s.DeEssFrequencyHz, (s,v) => s with { DeEssFrequencyHz = v }),
        new("GateThresholdDb", "Шумовой порог", "Чистота", -75, -30, 1, "dB", s => s.GateThresholdDb, (s,v) => s with { GateThresholdDb = v }),
        new("GateAttackMs", "Открытие микрофона", "Чистота", 1, 20, 1, "ms", s => s.GateAttackMs, (s,v) => s with { GateAttackMs = v }),
        new("GateReleaseMs", "Затухание в паузах", "Чистота", 50, 500, 5, "ms", s => s.GateReleaseMs, (s,v) => s with { GateReleaseMs = v }),
        new("GateRangeDb", "Ослабление шума", "Чистота", -60, -6, 1, "dB", s => s.GateRangeDb, (s,v) => s with { GateRangeDb = v }),
        new("ShadowMix", "Тёмный след", "Пространство", 0, .22f, .01f, "%", s => s.ShadowMix, (s,v) => s with { ShadowMix = v }),
        new("ShadowDelayMs", "Время следа", "Пространство", 12, 80, 1, "ms", s => s.ShadowDelayMs, (s,v) => s with { ShadowDelayMs = v }),
        new("RoomMix", "Пространство", "Пространство", 0, .12f, .01f, "%", s => s.RoomMix, (s,v) => s with { RoomMix = v }),
        new("RoomSize", "Размер комнаты", "Пространство", .05f, .5f, .01f, "", s => s.RoomSize, (s,v) => s with { RoomSize = v }),
        new("RoomDamping", "Поглощение комнаты", "Пространство", .3f, .95f, .01f, "", s => s.RoomDamping, (s,v) => s with { RoomDamping = v }),
        new("OutputGainDb", "Выходная громкость", "Выход", -12, 6, .1f, "dB", s => s.OutputGainDb, (s,v) => s with { OutputGainDb = v }),
        new("LimiterCeilingDb", "Защита от перегруза", "Выход", -6, -.5f, .1f, "dB", s => s.LimiterCeilingDb, (s,v) => s with { LimiterCeilingDb = v }),
    ];
}
