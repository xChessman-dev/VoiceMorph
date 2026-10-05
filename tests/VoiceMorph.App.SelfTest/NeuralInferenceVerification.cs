using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using VoiceMorph.App.Models;
using VoiceMorph.App.Services;

/// <summary>Opt-in real-model test. Uses synthetic input, never captures a microphone or sends audio to OBS.</summary>
public static class NeuralInferenceVerification
{
    public static async Task<int> RunAsync(string libraryDirectory, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var store = new NeuralModelStore(libraryDirectory);
        var library = await store.LoadAsync();
        if (library.Models.Count == 0) throw new InvalidOperationException("No imported models to verify.");
        var source = CreateSyntheticVoice(16000 * 2);
        var sourcePath = Path.Combine(outputDirectory, "synthetic-input.wav");
        WriteWave(sourcePath, source, 16000);
        var reports = new List<object>();
        var updatedModels = library.Models.ToList();
        foreach (var model in library.Models)
        {
            using var client = new NeuralRuntimeClient();
            var status = await client.ProbeAsync();
            Console.WriteLine($"Runtime: {status.Available} · CUDA {status.CudaAvailable} · {status.DeviceName}");
            if (!status.Available || !status.CudaAvailable) throw new InvalidOperationException("GPU runtime is not ready: " + status.Message);
            if (!await store.VerifyAssetsAsync(model)) throw new InvalidDataException("Model assets changed: " + model.Name);
            var options = new NeuralRuntimeOptions(Device: "cuda", IndexRate: 0, TrustIndex: false);
            var loadWatch = Stopwatch.StartNew();
            var inspection = await client.LoadModelAsync(model, options);
            loadWatch.Stop();
            var outputPath = Path.Combine(outputDirectory, model.Id.ToString("N") + ".wav");
            var rendered = await client.ConvertFileAsync(sourcePath, outputPath, options);
            using (var reader = new AudioFileReader(outputPath))
            {
                var samples = new float[(int)(reader.Length / 4)];
                var count = reader.Read(samples.AsSpan());
                AssertAudio(samples.AsSpan(0, count), "file render " + model.Name);
                if (Math.Abs(reader.TotalTime.TotalSeconds - 2) > .02) throw new InvalidDataException("Render changed duration.");
            }
            var processing = new List<double>();
            var wall = new List<double>();
            for (var index = 0; index < 12; index++)
            {
                var block = CreateSyntheticVoice(5120, index * 5120);
                var requestWatch = Stopwatch.StartNew();
                var result = await client.ConvertSamplesAsync(block, options);
                requestWatch.Stop();
                if (result.SampleRate != inspection.SampleRate || Math.Abs(result.Samples.Length - inspection.SampleRate * .32) > 1)
                    throw new InvalidDataException("Streaming block size does not match 320 ms.");
                AssertAudio(result.Samples, "live block " + model.Name);
                if (index >= 3) { processing.Add(result.ProcessingMs); wall.Add(requestWatch.Elapsed.TotalMilliseconds); }
            }
            var memory = client.WorkerWorkingSetBytes;
            reports.Add(new
            {
                model.Name, Inspection = inspection, status.DeviceName,
                LoadSeconds = loadWatch.Elapsed.TotalSeconds,
                RenderProcessingSeconds = rendered.ProcessingSeconds,
                BlockMilliseconds = 320,
                MeanInferenceMilliseconds = processing.Average(), MaximumInferenceMilliseconds = processing.Max(),
                MeanIpcMilliseconds = wall.Average(), MaximumIpcMilliseconds = wall.Max(),
                WorkerRamMiB = memory / 1048576d,
                IndexEnabled = false, SyntheticInput = true, MicrophoneCaptured = false,
            });
            updatedModels[updatedModels.FindIndex(item => item.Id == model.Id)] = model with { Inspection = inspection };
            await store.SaveAsync(new NeuralModelLibrary(1, updatedModels));
            Console.WriteLine($"PASS {model.Name}: {inspection.Version}, {inspection.SampleRate} Hz, load {loadWatch.Elapsed.TotalSeconds:0.0}s, " +
                $"320ms stream avg {processing.Average():0.0}ms, max {processing.Max():0.0}ms, RAM {memory / 1048576d:0}MiB");
        }
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS actual inference. Synthetic test only; naturalness and game FPS are not verified.");
        return 0;
    }

    private static void AssertAudio(ReadOnlySpan<float> samples, string label)
    {
        double energy = 0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample) || Math.Abs(sample) > .981) throw new InvalidDataException("Invalid audio: " + label);
            energy += sample * sample;
        }
        if (samples.Length == 0 || Math.Sqrt(energy / samples.Length) < .00001) throw new InvalidDataException("Silent output: " + label);
    }

    private static float[] CreateSyntheticVoice(int count, int offset = 0)
    {
        var result = new float[count];
        for (var index = 0; index < count; index++)
        {
            var time = (index + offset) / 16000d;
            var phase = 2 * Math.PI * (155 * time + .9 * Math.Sin(2 * Math.PI * 1.2 * time));
            var envelope = .6 + .4 * Math.Sin(2 * Math.PI * 2.8 * time);
            result[index] = (float)(envelope * (.18 * Math.Sin(phase) + .09 * Math.Sin(phase * 2) +
                .07 * Math.Sin(phase * 4) + .045 * Math.Sin(phase * 7) + .025 * Math.Sin(phase * 11)));
        }
        return result;
    }

    private static void WriteWave(string path, float[] samples, int sampleRate)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }
}
