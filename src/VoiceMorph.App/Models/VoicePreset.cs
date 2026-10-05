namespace VoiceMorph.App.Models;

public sealed record VoicePreset(
    string Id,
    string Name,
    string Description,
    string Character,
    float PitchSemitones,
    float Tone,
    float DriveDb,
    float GateThresholdDb,
    float Compression,
    float ShadowMix,
    float RoomMix)
{
    public static IReadOnlyList<VoicePreset> BuiltIns { get; } =
    [
        new("arbiter", "Арбитр", "Низкий, строгий и собранный", "CONTROL", -2.4f, -0.38f, 1.4f, -49f, 0.56f, 0.00f, 0.00f),
        new("strategist", "Холодный стратег", "Ровная подача и ясные согласные", "PRECISE", -1.1f, -0.18f, 0.4f, -52f, 0.68f, 0.00f, 0.00f),
        new("velvet", "Бархатная угроза", "Мягкий бас с близким присутствием", "DARK", -3.0f, -0.54f, 2.6f, -50f, 0.48f, 0.00f, 0.03f),
        new("entity", "Вторая сущность", "Тёмный след за естественным голосом", "DUAL", -1.8f, -0.32f, 1.8f, -53f, 0.58f, 0.18f, 0.02f),
        new("calm", "Нечеловеческое спокойствие", "Почти обычный, но тревожно стабильный", "STILL", -0.5f, -0.24f, 0.2f, -55f, 0.82f, 0.04f, 0.00f),
        new("oracle", "Оракул", "Чистый голос с воздушным пространством", "WIDE", -1.3f, 0.22f, 1.0f, -54f, 0.44f, 0.08f, 0.08f),
    ];
}
