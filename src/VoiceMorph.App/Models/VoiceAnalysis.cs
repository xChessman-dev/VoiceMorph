using System.Text.Json.Serialization;

namespace VoiceMorph.App.Models;

public sealed record VoiceAnalysis(
    double DurationSeconds,
    double VoicedSeconds,
    double MedianPitchHz,
    double PitchP10Hz,
    double PitchP90Hz,
    double PitchConfidence,
    double AverageRmsDb,
    double DynamicRangeDb,
    double PeakDb,
    double ClippedSampleRatio,
    double SpectralCentroidHz,
    double[] BandEnergyFractions,
    IReadOnlyList<string> Warnings)
{
    [JsonIgnore]
    public string Summary => $"{DurationSeconds:F0} с · голос {VoicedSeconds:F0} с · высота {MedianPitchHz:F0} Гц";
}

public sealed record AnalysisProgress(double Fraction, string Message);

public sealed record VoiceProfileFit(VoiceSettings Settings, IReadOnlyList<string> Notes);
