using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VoiceMorph.App.Models;
using VoiceMorph.App.Services;

internal static class ImportVerification
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "VoiceMorph-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var modelPath = Path.Combine(directory, "untrusted.pth");
            var indexPath = Path.Combine(directory, "untrusted.index");
            // Intentionally NOT a valid torch object: the raw C# importer must not try to deserialize it.
            byte[] modelBytes = [0x80, 0x04, 0x95, 0x01, 0x02, 0x03, 0x04, 0x05];
            byte[] indexBytes = [1, 2, 3, 4, 5];
            await File.WriteAllBytesAsync(modelPath, modelBytes);
            await File.WriteAllBytesAsync(indexPath, indexBytes);
            var store = new NeuralModelStore(Path.Combine(directory, "data"));
            var model = await store.RegisterAsync("Тест", modelPath, indexPath);
            check(model.Inspection is null && model.ModelBytes == modelBytes.Length && model.IndexBytes == indexBytes.Length,
                "RVC-импорт сохраняет сырые файлы без исполнения pickle");
            check(model.ModelSha256.Length == 64 && model.IndexSha256?.Length == 64 && await store.VerifyAssetsAsync(model),
                "RVC-модель и индекс получают проверяемые SHA-256");
            await store.SaveAsync(new(1, [model]));
            var loaded = await store.LoadAsync();
            check(loaded.Models.Count == 1 && loaded.Models[0].Id == model.Id && loaded.Models[0].IsAvailable,
                "RVC-библиотека восстанавливает локальные ссылки без изменения идентификаторов");
            await File.WriteAllBytesAsync(modelPath, [8, 7, 6, 5]);
            check(!await store.VerifyAssetsAsync(model), "Изменение RVC-файла обнаруживается по контрольной сумме");
            await File.WriteAllBytesAsync(modelPath, modelBytes);

            await RejectAsync(() => store.RegisterAsync(new string('a', 65), modelPath), check,
                "Название RVC-модели ограничено 64 символами");
            await RejectAsync(() => store.RegisterAsync("Тест", Path.Combine(directory, "unsafe.exe")), check,
                "Импорт не принимает исполняемый файл вместо .pth");
            await RejectAsync(() => store.RegisterAsync("Тест", "relative.pth"), check,
                "Импорт не принимает неоднозначный относительный путь");
            await RejectAsync(() => store.RegisterAsync("Тест", modelPath, inspection: new("v3", 48000, true, 1, 768)), check,
                "Неизвестная архитектура RVC не помечается проверенной");
            await RejectAsync(() => store.SaveAsync(new(1, [model, model])), check,
                "RVC-библиотека отклоняет повторяющиеся идентификаторы");
            await RejectAsync(() => store.SaveAsync(new(2, [model])), check,
                "RVC-библиотека отклоняет неизвестную версию JSON");

            var zipPath = Path.Combine(directory, "models.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                Add(zip, "nested/TestVoice.pth", modelBytes);
                Add(zip, "nested/added_IVF967_Flat_nprobe_1_v2.index", indexBytes);
                Add(zip, "readme.txt", Encoding.UTF8.GetBytes("Do not run other archive files."));
            }
            var archives = await store.InspectArchiveAsync(zipPath);
            check(archives.Count == 1 && archives[0].Name == "TestVoice" && archives[0].IndexEntry?.EndsWith(".index") == true,
                "ZIP-импорт связывает .pth с единственным индексом в его папке");
            var extracted = await store.ExtractArchiveAsync(zipPath, archives[0], Path.Combine(directory, "assets"));
            check((await File.ReadAllBytesAsync(extracted.ModelPath)).SequenceEqual(modelBytes) &&
                  (await File.ReadAllBytesAsync(extracted.IndexPath!)).SequenceEqual(indexBytes) &&
                  Directory.GetFiles(Path.GetDirectoryName(extracted.ModelPath)!).Length == 2,
                "ZIP извлекает только выбранную модель и индекс в новую отдельную папку");

            var traversal = Path.Combine(directory, "traversal.zip");
            using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Create))
                Add(zip, "../outside.pth", modelBytes);
            await RejectAsync(() => store.InspectArchiveAsync(traversal), check,
                "ZIP с выходом за папку назначения отклоняется до извлечения");
            var duplicate = Path.Combine(directory, "duplicate.zip");
            using (var zip = ZipFile.Open(duplicate, ZipArchiveMode.Create))
            {
                Add(zip, "model.pth", modelBytes);
                Add(zip, "MODEL.PTH", modelBytes);
            }
            await RejectAsync(() => store.InspectArchiveAsync(duplicate), check,
                "ZIP с конфликтующими именами Windows отклоняется");
            var link = Path.Combine(directory, "symlink.zip");
            using (var zip = ZipFile.Open(link, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("voice.pth");
                entry.ExternalAttributes = unchecked((int)(0xA000u << 16));
                using var stream = entry.Open();
                stream.Write(modelBytes);
            }
            await RejectAsync(() => store.InspectArchiveAsync(link), check,
                "ZIP с символической ссылкой вместо модели отклоняется");

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try
                {
                    await store.SaveAsync(new(1, []), cancellation.Token);
                    check(false, "Отменённый импорт сохраняет прежнюю RVC-библиотеку");
                }
                catch (OperationCanceledException)
                {
                    check((await store.LoadAsync()).Models.Count == 1, "Отменённый импорт сохраняет прежнюю RVC-библиотеку");
                }
            }

            var manifestPath = Path.Combine(directory, "data", "neural-models.json");
            var json = JsonSerializer.Serialize(new { Version = 1, Models = new[] { model }, UnknownCommand = "ignored?" });
            await File.WriteAllTextAsync(manifestPath, json);
            await RejectAsync(() => store.LoadAsync(), check, "Неизвестные поля RVC-манифеста отклоняются");
            check(!new NeuralRuntimeOptions().TrustIndex,
                "Нативный индекс FAISS по умолчанию не считается доверенным");
            using var missingClient = new NeuralRuntimeClient(directory, Path.Combine(directory, "missing-python.exe"), Path.Combine(directory, "worker.py"));
            var status = await missingClient.ProbeAsync();
            check(!status.Available && status.Message.Contains("не установлен"),
                "Недостающий нейродвижок показан честно без ложной готовности");
            var testPython = Environment.GetEnvironmentVariable("VOICEMORPH_TEST_PYTHON")
                ?? Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
            if (File.Exists(testPython))
            {
                var slowWorker = Path.Combine(directory, "slow_worker.py");
                await File.WriteAllTextAsync(slowWorker, "import sys,time\nfor line in sys.stdin:\n    time.sleep(30)\n", new UTF8Encoding(false));
                using (var pendingClient = new NeuralRuntimeClient(directory, testPython, slowWorker))
                using (var cancellation = new CancellationTokenSource())
                {
                    var pendingProbe = pendingClient.ProbeAsync(cancellation.Token);
                    var workerPid = pendingClient.WorkerProcessId;
                    cancellation.Cancel();
                    var canceled = false;
                    try { await pendingProbe; } catch (OperationCanceledException) { canceled = true; }
                    check(canceled && pendingClient.WorkerProcessId is null && await HasExitedAsync(workerPid),
                        "Отмена IPC завершает свой Python-worker и не оставляет процесс");
                }
                var noLeakedWorker = true;
                for (var iteration = 0; iteration < 8; iteration++)
                {
                    using var racingClient = new NeuralRuntimeClient(directory, testPython, slowWorker);
                    using var shutdown = new CancellationTokenSource();
                    var start = Task.Run(() => racingClient.ProbeAsync(shutdown.Token));
                    var close = Task.Run(racingClient.Dispose);
                    try { await Task.WhenAll(start, close).WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (TimeoutException)
                    {
                        noLeakedWorker = false;
                        shutdown.Cancel();
                        try { await start; } catch (OperationCanceledException) { }
                        await close;
                    }
                    noLeakedWorker &= racingClient.WorkerProcessId is null;
                    var afterClose = await racingClient.ProbeAsync();
                    noLeakedWorker &= !afterClose.Available && racingClient.WorkerProcessId is null;
                }
                check(noLeakedWorker,
                    "Параллельные запуск и Dispose не публикуют worker после закрытия клиента");
            }
        }
        finally
        {
            // A fresh GUID directory contains only files generated by this test.
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    private static async Task RejectAsync(Func<Task> operation, Action<bool, string> check, string message)
    {
        try { await operation(); check(false, message); }
        catch (InvalidDataException) { check(true, message); }
    }

    private static async Task<bool> HasExitedAsync(int? processId)
    {
        if (processId is null) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId.Value);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            return true;
        }
        catch (ArgumentException) { return true; }
        catch (TimeoutException) { return false; }
    }
}
