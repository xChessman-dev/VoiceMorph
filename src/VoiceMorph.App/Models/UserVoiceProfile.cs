namespace VoiceMorph.App.Models;

public sealed record UserVoiceProfile(
    Guid Id,
    string Name,
    VoiceSettings Settings,
    DateTime CreatedUtc,
    VoiceAnalysis? SourceAnalysis = null,
    VoiceAnalysis? TargetAnalysis = null,
    IReadOnlyList<string>? Notes = null);

public sealed record VoiceProfileData(
    int Version,
    VoiceAnalysis? Calibration,
    IReadOnlyList<UserVoiceProfile> Profiles)
{
    public static VoiceProfileData Empty => new(1, null, []);
}
