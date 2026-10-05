using VoiceMorph.App.Models;
using VoiceMorph.App.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 3 && args[0] == "--verify-neural-models")
    return await NeuralInferenceVerification.RunAsync(args[1], args[2]);

if (args.Length >= 4 && args[0] == "--import-neural-archives")
{
    // Headless raw-file import. This path never imports torch or deserializes .pth/.index.
    var store = new NeuralModelStore(args[1]);
    var library = await store.LoadAsync();
    var models = library.Models.ToList();
    foreach (var archivePath in args.Skip(3))
    {
        foreach (var selection in await store.InspectArchiveAsync(archivePath))
        {
            if (models.Any(model => model.Name.Equals(selection.Name, StringComparison.OrdinalIgnoreCase)))
            { Console.WriteLine("Already imported: " + selection.Name); continue; }
            var extracted = await store.ExtractArchiveAsync(archivePath, selection, args[2]);
            var model = await store.RegisterAsync(selection.Name, extracted.ModelPath, extracted.IndexPath);
            models.Add(model);
            await store.SaveAsync(new NeuralModelLibrary(1, models));
            Console.WriteLine("Imported raw model: " + model.Name + " · " + model.Summary);
        }
    }
    return 0;
}

var failures = new List<string>();

void Check(bool condition, string message)
{
    Console.WriteLine($"{(condition ? "[OK]" : "[FAIL]")} {message}");
    if (!condition)
    {
        failures.Add(message);
    }
}

Console.WriteLine("VoiceMorph — самопроверка\n");

DspVerification.Run(Check);
await AnalysisVerification.RunAsync(Check);
await ImportVerification.RunAsync(Check);
if (args.Contains("--devices"))
{
    await NeuralAudioVerification.RunAsync(Check);
    await PreviewVerification.RunAsync(Check);
}

var presets = VoicePreset.BuiltIns;
Check(presets.Count == 6, "Доступно шесть авторских голосовых профилей");
Check(presets.Select(preset => preset.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == presets.Count,
    "Идентификаторы профилей уникальны");
Check(presets.All(preset => preset.PitchSemitones is >= -6 and <= 4),
    "Высота голоса каждого профиля находится в безопасном диапазоне");
Check(presets.All(preset => preset.ShadowMix is >= 0 and <= 0.35f),
    "Эффект тёмного следа каждого профиля находится в безопасном диапазоне");

if (args.Contains("--devices"))
{
using var engine = new AudioEngine();
var inputs = AudioEngine.GetInputDevices();
var outputs = AudioEngine.GetOutputDevices();

Check(inputs.Count > 0, $"Найдены устройства ввода: {inputs.Count}");
Check(outputs.Count > 0, $"Найдены устройства вывода: {outputs.Count}");

var preferredInput = inputs.FirstOrDefault(device =>
        device.Name.Contains("PD200X", StringComparison.OrdinalIgnoreCase))
    ?? inputs.FirstOrDefault(device =>
        device.Name.Contains("Maono", StringComparison.OrdinalIgnoreCase));
var preferredOutput = outputs.FirstOrDefault(device =>
    device.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) &&
    !device.Name.Contains("16ch", StringComparison.OrdinalIgnoreCase));

Check(preferredInput is not null, "Найден основной микрофон PD200X / Maono");
Check(preferredOutput is not null, "Найден виртуальный выход VB-CABLE");

if (preferredInput is not null && preferredOutput is not null)
{
    try
    {
        engine.ApplySettings(VoiceSettings.FromPreset(presets[0]));
        await engine.StartAsync(preferredInput.Id, preferredOutput.Id);
        await Task.Delay(900);
        Check(engine.IsRunning, "Аудиотракт запускается в реальном времени");
        Check(engine.ReportedLatencyMs > 0, $"Расчётная задержка DSP + WASAPI ≈ {engine.ReportedLatencyMs} мс");
        engine.SetBypass(true);
        await Task.Delay(120);
        Check(engine.IsBypassed, "Мгновенный обход эффектов работает");
        engine.Stop();
        Check(!engine.IsRunning, "Аудиотракт корректно останавливается");
    }
    catch (Exception exception)
    {
        failures.Add("Реальный аудиотракт: " + exception.Message);
        Console.WriteLine($"[FAIL] Реальный аудиотракт: {exception.Message}");
        engine.Stop();
    }
}

}
else Console.WriteLine("SKIP: hardware checks; enable with --devices.");

Console.WriteLine();
if (failures.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Все проверки пройдены.");
    Console.ResetColor();
    return 0;
}

Console.ForegroundColor = ConsoleColor.Red;
Console.WriteLine($"Проверок с ошибками: {failures.Count}");
foreach (var failure in failures)
{
    Console.WriteLine(" - " + failure);
}
Console.ResetColor();
return 1;
