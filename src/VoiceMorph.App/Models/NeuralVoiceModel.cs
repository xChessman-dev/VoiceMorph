using System.IO;

namespace VoiceMorph.App.Models;

/// <summary>Metadata only. A .pth checkpoint is never deserialized in the UI process.</summary>
public sealed record NeuralVoiceModel(
    Guid Id, string Name, string ModelPath, string? IndexPath,
    long ModelBytes, long IndexBytes, string ModelSha256, string? IndexSha256,
    DateTime ImportedUtc, NeuralModelInspection? Inspection = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAvailable => File.Exists(ModelPath) && (IndexPath is null || File.Exists(IndexPath));
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => Inspection is null
        ? $"RVC · импортирован · {(ModelBytes + IndexBytes) / (1024d * 1024d):0.#} МБ"
        : $"RVC {Inspection.Version} · {Inspection.SampleRate / 1000} кГц · {(Inspection.UsesF0 ? "с высотой голоса" : "без F0")}";
}

public sealed record NeuralModelInspection(string Version, int SampleRate, bool UsesF0,
    int SpeakerCount, int FeatureDimension);

public sealed record NeuralArchiveModel(string Name, string ModelEntry, string? IndexEntry,
    long ModelBytes, long IndexBytes);

public sealed record NeuralModelLibrary(int Version, IReadOnlyList<NeuralVoiceModel> Models)
{
    public static NeuralModelLibrary Empty => new(1, []);
}

public sealed record NeuralRuntimeStatus(bool Available, string Message, bool CudaAvailable = false,
    string? DeviceName = null);

public sealed record NeuralRuntimeOptions(int PitchSemitones = 0, float IndexRate = .5f,
    float Protect = .33f, string Device = "auto", int SpeakerId = 0, bool TrustIndex = false);

public sealed record NeuralAudioResult(float[] Samples, int SampleRate, double ProcessingMs);

public sealed record NeuralConversionResult(string OutputPath, double ProcessingSeconds,
    double AudioSeconds, string Device);
