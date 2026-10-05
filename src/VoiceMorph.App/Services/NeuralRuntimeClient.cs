using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using VoiceMorph.App.Models;

namespace VoiceMorph.App.Services;

/// <summary>
/// Single-flight JSON-lines IPC. Neural code stays in an isolated Python worker and is
/// started only when requested. Cancellation kills our worker to prevent stale responses.
/// </summary>
public sealed class NeuralRuntimeClient : IDisposable, IAsyncDisposable
{
    public static string DefaultRuntimeRoot => Environment.GetEnvironmentVariable("VOICEMORPH_RUNTIME_ROOT")
        ?? Path.Combine(AppContext.BaseDirectory, "runtime", "rvc");
    private const int MaximumResponseCharacters = 8 * 1024 * 1024;
    private readonly string _runtimeRoot;
    private readonly string? _pythonPath;
    private readonly string _workerPath;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly object _workerSync = new();
    private readonly Queue<string> _diagnostics = new();
    private Process? _process;
    private string? _activePythonPath;
    private bool _disposed;
    private bool _loaded;

    public int? WorkerProcessId
    {
        get
        {
            try { return _process is { HasExited: false } process ? process.Id : null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public long WorkerWorkingSetBytes
    {
        get
        {
            try
            {
                var process = _process;
                if (process is null || process.HasExited) return 0;
                process.Refresh();
                return process.WorkingSet64;
            }
            catch (InvalidOperationException) { return 0; }
            catch (System.ComponentModel.Win32Exception) { return 0; }
        }
    }

    public TimeSpan WorkerCpuTime
    {
        get
        {
            try { return _process is { HasExited: false } process ? process.TotalProcessorTime : TimeSpan.Zero; }
            catch (InvalidOperationException) { return TimeSpan.Zero; }
            catch (System.ComponentModel.Win32Exception) { return TimeSpan.Zero; }
        }
    }

    public NeuralRuntimeClient(string? runtimeRoot = null, string? pythonPath = null, string? workerPath = null)
    {
        _runtimeRoot = Path.GetFullPath(runtimeRoot ?? DefaultRuntimeRoot);
        pythonPath ??= Environment.GetEnvironmentVariable("VOICEMORPH_PYTHON");
        _pythonPath = pythonPath is null ? null : Path.GetFullPath(pythonPath);
        _workerPath = Path.GetFullPath(workerPath ?? Path.Combine(AppContext.BaseDirectory, "runtime", "rvc_worker.py"));
    }

    public async Task<NeuralRuntimeStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var python = _pythonPath ?? FindPython(_runtimeRoot);
        if (python is null || !File.Exists(python))
            return new(false, "Нейродвижок не установлен. DSP работает без него.");
        if (!File.Exists(_workerPath))
            return new(false, "Не найден локальный RVC-worker. Проверьте комплект приложения.");
        try
        {
            var result = await RequestAsync("info", [], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            var ready = ReadBoolean(result, "ready");
            var message = ReadString(result, "message") ?? (ready ? "Локальный RVC готов." : "Для RVC не хватает зависимостей или служебных моделей.");
            var cuda = ReadBoolean(result, "cuda_available");
            return new(ready, message, cuda, ReadString(result, "device_name") ?? ReadString(result, "gpu_name"));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return new(false, exception.Message);
        }
    }

    public async Task<NeuralModelInspection> InspectAsync(string modelPath, string? indexPath = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("inspect", new()
        {
            ["model_path"] = Path.GetFullPath(modelPath),
            ["index_path"] = indexPath is null ? null : Path.GetFullPath(indexPath),
            ["trust_index"] = false,
        }, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        return ParseInspection(result);
    }

    public async Task<NeuralModelInspection> LoadModelAsync(NeuralVoiceModel model, NeuralRuntimeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!model.IsAvailable) throw new FileNotFoundException("Файлы выбранной RVC-модели отсутствуют. Импортируйте их повторно.");
        var request = Options(options ?? new());
        request["model_path"] = model.ModelPath;
        request["index_path"] = model.IndexPath;
        var result = await RequestAsync("load", request, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        var inspection = ParseInspection(result);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_workerSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loaded = true;
        }
        return inspection;
    }

    /// <summary>Input is mono IEEE float32 at 16 kHz; the worker returns the model's sample rate.</summary>
    public async Task<NeuralAudioResult> ConvertSamplesAsync(float[] samples, NeuralRuntimeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        if (samples.Length is < 400 or > 16000 || samples.Any(sample => !float.IsFinite(sample)))
            throw new ArgumentException("Ожидается блок от 25 мс до 1 секунды: 400–16000 конечных mono-сэмплов 16 кГц.", nameof(samples));
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        var request = Options(options ?? new());
        request["samples_base64"] = Convert.ToBase64String(bytes);
        request["sample_rate"] = 16000;
        request["history_seconds"] = 2;
        var result = await RequestAsync("convertSamples", request, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        var sampleRate = ReadInteger(result, "sample_rate");
        if (sampleRate is not (32000 or 40000 or 48000))
            throw new InvalidDataException("RVC-worker вернул неподдерживаемую частоту аудио.");
        var encoded = ReadString(result, "samples_base64") ?? throw new InvalidDataException("RVC-worker не вернул аудио.");
        if (encoded.Length > 1024 * 1024) throw new InvalidDataException("RVC-worker вернул слишком большой аудиоблок.");
        byte[] outputBytes;
        try { outputBytes = Convert.FromBase64String(encoded); }
        catch (FormatException exception) { throw new InvalidDataException("Повреждённый аудиоблок RVC.", exception); }
        if (outputBytes.Length == 0 || outputBytes.Length % sizeof(float) != 0 || outputBytes.Length > sampleRate * sizeof(float) * 2 + 4096)
            throw new InvalidDataException("RVC-worker вернул неверную длину аудиоблока.");
        var output = new float[outputBytes.Length / sizeof(float)];
        Buffer.BlockCopy(outputBytes, 0, output, 0, outputBytes.Length);
        if (output.Any(sample => !float.IsFinite(sample)))
            throw new InvalidDataException("RVC-worker вернул нечисловые значения аудио.");
        // Untrusted models cannot bypass the final output headroom.
        for (var i = 0; i < output.Length; i++) output[i] = Math.Clamp(output[i], -.98f, .98f);
        var processingMs = ReadDouble(result, "processing_ms");
        if (!double.IsFinite(processingMs) || processingMs < 0)
            throw new InvalidDataException("RVC-worker вернул неверное время обработки.");
        return new(output, sampleRate, processingMs);
    }

    public async Task<NeuralConversionResult> ConvertFileAsync(string inputPath, string outputPath,
        NeuralRuntimeOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        var request = Options(options ?? new());
        request["input_path"] = Path.GetFullPath(inputPath);
        request["output_path"] = Path.GetFullPath(outputPath);
        var result = await RequestAsync("render", request, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        return new(ReadString(result, "output_path") ?? Path.GetFullPath(outputPath),
            result.TryGetProperty("processing_seconds", out _) ? ReadDouble(result, "processing_seconds") : ReadDouble(result, "processing_ms") / 1000,
            result.TryGetProperty("audio_seconds", out _) ? ReadDouble(result, "audio_seconds") : ReadDouble(result, "duration_seconds"),
            ReadString(result, "device") ?? "неизвестно");
    }

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        lock (_workerSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is null || _process.HasExited) { _loaded = false; return; }
        }
        await RequestAsync("unload", [], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        lock (_workerSync) _loaded = false;
    }

    private async Task<JsonElement> RequestAsync(string command, Dictionary<string, object?> values,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var process = StartWorker();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var id = Guid.NewGuid().ToString("N");
            values["id"] = id;
            values["command"] = command;
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(values).AsMemory(), deadline.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                var line = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                if (line is null)
                    throw new IOException("RVC-worker завершился до ответа." + DiagnosticSuffix());
                if (line.Length > MaximumResponseCharacters) throw new InvalidDataException("Ответ RVC-worker превышает допустимый размер.");
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || ReadString(root, "id") != id)
                    throw new InvalidDataException("RVC-worker вернул ответ не на текущий запрос.");
                if (!ReadBoolean(root, "ok"))
                    throw new InvalidOperationException(ReadString(root, "error") ?? "RVC-worker не выполнил запрос.");
                return root.Clone();
            }
            catch (OperationCanceledException)
            {
                StopWorker();
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("RVC не ответил вовремя. Движок остановлен; DSP остаётся доступным.");
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                StopWorker();
                if (exception is JsonException) throw new InvalidDataException("Неверный JSON от RVC-worker.", exception);
                throw;
            }
        }
        finally { _requests.Release(); }
    }

    private Process StartWorker()
    {
        lock (_workerSync)
        {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var python = _pythonPath ?? FindPython(_runtimeRoot);
        if (_process is { HasExited: false } && _activePythonPath == python) return _process;
        StopWorkerCore();
        if (python is null || !File.Exists(python)) throw new FileNotFoundException("Python нейродвижка не установлен на F:.");
        if (!File.Exists(_workerPath)) throw new FileNotFoundException("Не найден локальный RVC-worker.");
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false), WorkingDirectory = Path.GetDirectoryName(_workerPath)!,
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(_workerPath);
        start.ArgumentList.Add("--runtime-root");
        start.ArgumentList.Add(_runtimeRoot);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["OMP_NUM_THREADS"] = "2";
        start.Environment["MKL_NUM_THREADS"] = "2";
        start.Environment["NUMBA_NUM_THREADS"] = "2";
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["HF_HOME"] = Path.Combine(_runtimeRoot, "cache");
        start.Environment["TORCH_HOME"] = Path.Combine(_runtimeRoot, "cache");
        start.Environment["TEMP"] = Path.Combine(_runtimeRoot, "tmp");
        start.Environment["TMP"] = Path.Combine(_runtimeRoot, "tmp");
        var process = new Process { StartInfo = start };
        process.ErrorDataReceived += (_, arguments) =>
        {
            if (string.IsNullOrWhiteSpace(arguments.Data)) return;
            lock (_diagnostics)
            {
                _diagnostics.Enqueue(arguments.Data[..Math.Min(512, arguments.Data.Length)]);
                while (_diagnostics.Count > 6) _diagnostics.Dequeue();
            }
        };
        try
        {
            process.Start();
            _process = process;
            _activePythonPath = python;
            process.BeginErrorReadLine();
            return process;
        }
        catch
        {
            if (ReferenceEquals(_process, process)) StopWorkerCore();
            else process.Dispose();
            throw;
        }
        }
    }

    private string DiagnosticSuffix()
    {
        lock (_diagnostics) return _diagnostics.Count == 0 ? "" : " " + _diagnostics.Last();
    }

    private static string? FindPython(string root)
    {
        var paths = new[]
        {
            Path.Combine(root, "python", "python.exe"),
            Path.Combine(root, "venv", "Scripts", "python.exe"),
            Path.Combine(root, "Scripts", "python.exe"),
        };
        return paths.FirstOrDefault(File.Exists);
    }

    private static Dictionary<string, object?> Options(NeuralRuntimeOptions options)
    {
        if (options.PitchSemitones is < -24 or > 24 || !float.IsFinite(options.IndexRate) || options.IndexRate is < 0 or > 1 ||
            !float.IsFinite(options.Protect) || options.Protect is < 0 or > .5f || options.SpeakerId is < 0 or > 1023 ||
            options.Device is not ("auto" or "cpu" or "cuda"))
            throw new ArgumentException("Некорректные параметры RVC.", nameof(options));
        return new()
        {
            ["pitch_semitones"] = options.PitchSemitones, ["index_rate"] = options.TrustIndex ? options.IndexRate : 0,
            ["protect"] = options.Protect, ["device"] = options.Device, ["speaker_id"] = options.SpeakerId,
            ["trust_index"] = options.TrustIndex,
        };
    }

    private static NeuralModelInspection ParseInspection(JsonElement result)
    {
        if (!result.TryGetProperty("model", out var model)) throw new InvalidDataException("Worker не вернул метаданные модели.");
        var inspection = new NeuralModelInspection(ReadString(model, "version") ?? "",
            ReadInteger(model, "sample_rate"), ReadBoolean(model, "uses_f0"),
            ReadInteger(model, "speaker_count"), ReadInteger(model, "feature_dimension"));
        NeuralModelStore.ValidateInspection(inspection);
        return inspection;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool ReadBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static int ReadInteger(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;
    private static double ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private void EnsureLoaded()
    {
        lock (_workerSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_loaded) throw new InvalidOperationException("Сначала загрузите RVC-модель.");
        }
    }

    private void StopWorker()
    {
        lock (_workerSync) StopWorkerCore();
    }

    // The worker lock covers publication, startup and shutdown as one lifecycle.
    // Dispose cannot miss a process between Process.Start() and assignment.
    private void StopWorkerCore()
    {
        var process = _process;
        _process = null;
        _activePythonPath = null;
        _loaded = false;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }

    public void Dispose()
    {
        lock (_workerSync)
        {
            if (_disposed) return;
            _disposed = true;
            StopWorkerCore();
        }
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
