using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.Wave;
using VoiceMorph.App.Models;
using VoiceMorph.App.Services;

/// <summary>
/// Verifies the live audio plumbing using an isolated, dependency-free fake RVC
/// worker. It captures no files and emits silence only to VB-CABLE. This does not
/// claim real-model voice quality, CUDA performance or measured loopback latency.
/// </summary>
public static class NeuralAudioVerification
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, ".neural-live-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VerifyOutputBuffer(check);
            var dropCount = 0;
            var queue = Channel.CreateBounded<int>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest }, _ => dropCount++);
            for (var index = 1; index <= 5; index++) queue.Writer.TryWrite(index);
            check(queue.Reader.TryRead(out var latest) && latest == 5 && dropCount == 4,
                "RVC-очередь хранит только свежий блок, без накопления старой речи");

            var python = Environment.GetEnvironmentVariable("VOICEMORPH_TEST_PYTHON")
                ?? Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
            if (!File.Exists(python))
            {
                check(false, "Для тестового RVC-worker нужен существующий переносимый Python");
                return;
            }
            var workerPath = Path.Combine(root, "fake_worker.py");
            await File.WriteAllTextAsync(workerPath, FakeWorker);
            var inputs = AudioEngine.GetInputDevices();
            var outputs = AudioEngine.GetOutputDevices();
            var microphone = inputs.FirstOrDefault(device => device.Name.Contains("PD200X", StringComparison.OrdinalIgnoreCase))
                ?? inputs.FirstOrDefault(device => device.Name.Contains("Maono", StringComparison.OrdinalIgnoreCase));
            var cable = outputs.FirstOrDefault(device => device.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) &&
                                                       !device.Name.Contains("16ch", StringComparison.OrdinalIgnoreCase));
            if (microphone is null || cable is null)
            {
                check(false, "Для аппаратного RVC-теста нужны PD200X / Maono и стерео VB-CABLE");
                return;
            }

            await using var client = new NeuralRuntimeClient(root, python, workerPath);
            await using var engine = new NeuralAudioEngine();
            var maximumOutput = 0f;
            engine.MeterUpdated += (_, meter) => maximumOutput = Math.Max(maximumOutput, meter.Output);
            var telemetryCount = 0;
            var lagNotified = false;
            engine.TelemetryUpdated += (_, telemetry) =>
            {
                Interlocked.Increment(ref telemetryCount);
                lagNotified |= telemetry.CannotKeepUp;
            };
            var normal = await ModelAsync(root, "normal");
            await engine.StartAsync(microphone.Id, cable.Id, normal, new(), 200, client);
            await Task.Delay(900);
            check(engine.IsRunning && telemetryCount > 0 && engine.ReportedLatencyMs >= 200,
                "Живой RVC-тракт работает: WASAPI → mono 16 кГц → worker → стерео VB-CABLE");
            check(maximumOutput == 0, "Тестовый worker подаёт только тишину: прямой микрофон не попадает в выход");
            await engine.StopAsync();
            await engine.StopAsync();
            check(!engine.IsRunning && engine.ReportedLatencyMs == 0, "Повторный Stop RVC освобождает оба WASAPI-устройства");

            lagNotified = false;
            var slow = await ModelAsync(root, "slow");
            await engine.StartAsync(microphone.Id, cable.Id, slow, new(), 200, client);
            await Task.Delay(1850);
            check(engine.DroppedBlocks > 0 && lagNotified && engine.BufferedMilliseconds < 1500,
                "Медленный RVC-worker отбрасывает устаревшие блоки и сообщает, что не успевает");
            await engine.StopAsync();

            var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.EngineFaulted += (_, message) => failure.TrySetResult(message);
            maximumOutput = 0;
            var broken = await ModelAsync(root, "fail");
            await engine.StartAsync(microphone.Id, cable.Id, broken, new(), 200, client);
            var faultMessage = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await engine.StopAsync();
            check(faultMessage.Contains("synthetic inference failure", StringComparison.Ordinal) && !engine.IsRunning && maximumOutput == 0,
                "Ошибка inference останавливает RVC, без автоматической подмены обычным голосом");

            var cancellable = await ModelAsync(root, "cancel");
            await engine.StartAsync(microphone.Id, cable.Id, cancellable, new(), 200, client);
            await Task.Delay(500);
            var stopWatch = System.Diagnostics.Stopwatch.StartNew();
            await engine.StopAsync();
            check(!engine.IsRunning && stopWatch.Elapsed < TimeSpan.FromSeconds(3) && client.WorkerProcessId is null,
                "Stop отменяет зависший inference, завершает свой worker и не ждёт UI-callback");
        }
        catch (Exception exception)
        {
            check(false, "Проверка живого RVC-тракта: " + exception.Message);
        }
        finally
        {
            // Only this test's newly-created GUID child directory is removed.
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task<NeuralVoiceModel> ModelAsync(string root, string name)
    {
        var path = Path.Combine(root, name + ".pth");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        return new(Guid.NewGuid(), name, path, null, 4, 0, new string('a', 64), null,
            DateTime.UtcNow, new("v2", 40000, true, 1, 768));
    }

    private static void VerifyOutputBuffer(Action<bool, string> check)
    {
        var providerType = typeof(NeuralAudioEngine).GetNestedType("PrefilledOutputProvider", BindingFlags.NonPublic)!;
        var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(40000, 1), TimeSpan.FromSeconds(2))
        { ReadFully = true, DiscardOnBufferOverflow = false };
        var source = (ISampleProvider)Activator.CreateInstance(providerType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, [buffer, 12800], null)!;
        var output = new float[640];
        source.Read(output);
        check(output.All(value => value == 0), "Пустой RVC-выход воспроизводит тишину");
        var halfBlock = Enumerable.Repeat(.2f, 6400).ToArray();
        buffer.AddSamples(MemoryMarshal.AsBytes(halfBlock.AsSpan()));
        source.Read(output);
        check(output.All(value => value == 0) && buffer.BufferedBytes == 6400 * sizeof(float),
            "RVC-проигрыватель ждёт полный блок вместо преждевременного старта");
        buffer.AddSamples(MemoryMarshal.AsBytes(halfBlock.AsSpan()));
        source.Read(output);
        check(output.All(value => Math.Abs(value - .2f) < .00001f), "После prefill проигрывается именно обработанный блок");
        source.Read(new float[14000]);
        source.Read(output);
        var underruns = (int)providerType.GetProperty("Underruns")!.GetValue(source)!;
        buffer.AddSamples(MemoryMarshal.AsBytes(halfBlock.AsSpan()));
        source.Read(output);
        check(underruns == 1 && output.All(value => value == 0),
            "Underrun считается один раз и повторно включает безопасный prefill");

        var guarded = new float[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -3, 3, .2f };
        var sanitizer = typeof(NeuralAudioEngine).GetMethod("SanitizeOutput", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<SampleSanitizer>();
        sanitizer(guarded);
        check(guarded.All(float.IsFinite) && guarded.All(value => Math.Abs(value) <= .891f) && guarded[0] == 0 && guarded[5] == .2f,
            "Выход RVC защищён от NaN, бесконечности и уровня выше −1 dB");
    }

    private delegate void SampleSanitizer(Span<float> samples);

    private const string FakeWorker = """
import array, base64, json, pathlib, sys, time
mode = 'normal'
metadata = dict(version='v2', sample_rate=40000, uses_f0=True, speaker_count=1, feature_dimension=768)
for line in sys.stdin:
    request = json.loads(line)
    reply = dict(id=request['id'], ok=True)
    try:
        command = request['command']
        if command in ('inspect', 'load'):
            mode = pathlib.Path(request['model_path']).stem
            reply['model'] = metadata
        elif command == 'convertSamples':
            if mode == 'slow': time.sleep(.75)
            if mode == 'cancel': time.sleep(20)
            if mode == 'fail': raise RuntimeError('synthetic inference failure')
            length = len(base64.b64decode(request['samples_base64'])) // 4
            zeros = array.array('f', [0.0]) * round(length * 40000 / 16000)
            reply.update(samples_base64=base64.b64encode(zeros.tobytes()).decode('ascii'), sample_rate=40000, processing_ms=0)
        elif command == 'info': reply.update(ready=True, cuda_available=False, message='synthetic test worker')
    except Exception as error:
        reply.update(ok=False, error=str(error))
    print(json.dumps(reply), flush=True)
""";
}
