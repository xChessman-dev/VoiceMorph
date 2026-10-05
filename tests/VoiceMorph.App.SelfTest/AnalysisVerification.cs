using NAudio.Wave;
using VoiceMorph.App.Models;
using VoiceMorph.App.Services;

internal static class AnalysisVerification
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const int rate = 16000;
        var analyzer = new VoiceAnalyzer();
        static float[] Sine(double frequency, int sampleRate, double seconds) =>
            Enumerable.Range(0, (int)(sampleRate * seconds))
                .Select(i => (float)(0.18 * Math.Sin(2 * Math.PI * frequency * i / sampleRate) +
                                     0.04 * Math.Sin(4 * Math.PI * frequency * i / sampleRate)))
                .ToArray();

        var source = analyzer.AnalyzeSamples(Sine(160, rate, 2), rate);
        var target = analyzer.AnalyzeSamples(Sine(120, rate, 2), rate);
        check(Math.Abs(source.MedianPitchHz - 160) < 2 && Math.Abs(target.MedianPitchHz - 120) < 2,
            "Акустический анализ определяет 160 и 120 Гц без октавной ошибки");
        check(source.PitchConfidence > 0.95 && source.VoicedSeconds > 1.7,
            "Анализ считает длительность устойчивого голоса и уверенность высоты");
        check(Math.Abs(source.BandEnergyFractions.Sum() - 1) < 1e-6 && source.ClippedSampleRatio == 0,
            "Спектральные доли нормированы; чистый сигнал не объявлен перегруженным");

        try
        {
            analyzer.AnalyzeSamples(new float[rate * 2], rate);
            check(false, "Тишина отклонена до создания профиля");
        }
        catch (InvalidDataException) { check(true, "Тишина отклонена до создания профиля"); }

        var random = new Random(7);
        var noise = Enumerable.Range(0, rate * 2).Select(_ => (float)(random.NextDouble() * 0.2 - 0.1)).ToArray();
        try
        {
            analyzer.AnalyzeSamples(noise, rate);
            check(false, "Непериодичный шум отклонён вместо ложного голосового профиля");
        }
        catch (InvalidDataException) { check(true, "Непериодичный шум отклонён вместо ложного голосового профиля"); }

        try
        {
            analyzer.FitProfile(source, target);
            check(false, "Короткие калибровка и пример не проходят требования профиля");
        }
        catch (InvalidDataException) { check(true, "Короткие калибровка и пример не проходят требования профиля"); }

        source = source with { DurationSeconds = 30, VoicedSeconds = 25 };
        target = target with { DurationSeconds = 120, VoicedSeconds = 100 };
        var fit = analyzer.FitProfile(source, target, 0.6f);
        check(fit.Settings.PitchSemitones is >= -3.001f and < -2.9f && Math.Abs(fit.Settings.Strength - 0.6f) < 1e-6,
            "Подбор ограничивает большую разницу высоты и не применяет силу преобразования дважды");
        check(fit.Settings.RoomMix == 0 && fit.Settings.ShadowMix == 0 && fit.Notes.Count > 0,
            "Профиль по примеру оставляет речь сухой и сообщает границы метода");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            analyzer.AnalyzeSamples(Sine(150, rate, 2), rate, cancelled.Token);
            check(false, "Акустический анализ поддерживает отмену");
        }
        catch (OperationCanceledException) { check(true, "Акустический анализ поддерживает отмену"); }

        var directory = Path.Combine(AppContext.BaseDirectory, "analysis-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "voice-profiles.json");
        var profilePath = Path.Combine(directory, "profile.json");
        var wavePath = Path.Combine(directory, "reference.wav");
        try
        {
            var store = new VoiceProfileStore(directory);
            var profile = new UserVoiceProfile(Guid.NewGuid(), "Проверка голоса", fit.Settings,
                DateTime.UtcNow, source, target, fit.Notes);
            await store.SaveAsync(new(1, source, [profile]));
            var loaded = await store.LoadAsync();
            check(loaded.Profiles.Count == 1 && loaded.Calibration?.MedianPitchHz == source.MedianPitchHz &&
                  loaded.Profiles[0].Settings == profile.Settings,
                "Профили и калибровка сохраняются и загружаются без потери настроек");
            var previousBytes = await File.ReadAllBytesAsync(storePath);
            try
            {
                await store.SaveAsync(new(1, null, []), cancelled.Token);
                check(false, "Отменённое сохранение не заменяет успешное хранилище");
            }
            catch (OperationCanceledException)
            {
                check((await File.ReadAllBytesAsync(storePath)).SequenceEqual(previousBytes),
                    "Отменённое сохранение не заменяет успешное хранилище");
            }

            await Task.Run(async () =>
            {
                var writes = Enumerable.Range(0, 12).Select(index =>
                    store.SaveAsync(new(1, source, [profile with { Name = "Параллельный " + index }])));
                var reads = Enumerable.Range(0, 20).Select(async _ =>
                {
                    var snapshot = await store.LoadAsync();
                    if (snapshot.Profiles.Count != 1) throw new IOException("Неполный снимок профиля.");
                });
                await Task.WhenAll(writes.Concat(reads));
            });
            check((await store.LoadAsync()).Profiles.Count == 1 &&
                  !Directory.EnumerateFiles(directory, ".voicemorph-*.tmp").Any(),
                "Параллельные чтения и записи сохраняют целый JSON без оставшихся временных файлов");

            var synchronousClose = Task.Factory.StartNew(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
                store.SaveAsync(new(1, source, [profile])).GetAwaiter().GetResult();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await synchronousClose.WaitAsync(TimeSpan.FromSeconds(10));
            check(true, "Синхронное сохранение при закрытии окна не требует UI-контекста и не зависает");

            await store.ExportAsync(profile, profilePath);
            var imported = await store.ImportAsync(profilePath);
            check(imported.Id != profile.Id && imported.Name == profile.Name && imported.Settings == profile.Settings,
                "Импорт создаёт отдельный профиль; экспорт сохраняет параметры");

            using (var writer = new WaveFileWriter(wavePath, new WaveFormat(44100, 16, 2)))
            {
                var samples = Sine(220, 44100, 2);
                foreach (var sample in samples)
                {
                    writer.WriteSample(sample);
                    writer.WriteSample(sample);
                }
            }
            var decoded = await analyzer.AnalyzeFileAsync(wavePath);
            check(Math.Abs(decoded.MedianPitchHz - 220) < 2 && Math.Abs(decoded.DurationSeconds - 2) < 0.02,
                "Файловый анализ читает стерео WAV, сводит каналы и пересчитывает частоту дискретизации");

            await File.WriteAllTextAsync(profilePath, "{\"Version\":999,\"Profile\":null}");
            try
            {
                await store.ImportAsync(profilePath);
                check(false, "Неподдерживаемая версия импорта отклоняется");
            }
            catch (InvalidDataException) { check(true, "Неподдерживаемая версия импорта отклоняется"); }

            var calibrationPath = Path.Combine(directory, "calibration.wav");
            File.Copy(wavePath, calibrationPath);
            var successfulCalibration = await File.ReadAllBytesAsync(calibrationPath);
            try
            {
                await new CalibrationRecorder().CaptureAsync("not-opened-after-cancellation", calibrationPath,
                    TimeSpan.FromSeconds(30), null, cancelled.Token);
                check(false, "Отмена записи до запуска устройства сохраняет предыдущую калибровку");
            }
            catch (OperationCanceledException)
            {
                check((await File.ReadAllBytesAsync(calibrationPath)).SequenceEqual(successfulCalibration),
                    "Отмена записи до запуска устройства сохраняет предыдущую калибровку");
            }
            finally { File.Delete(calibrationPath); }
        }
        finally
        {
            // All targets are exact files generated above in a unique test-only folder.
            foreach (var path in new[] { storePath, profilePath, wavePath })
                if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory, recursive: false);
        }
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            // A closed/non-pumping UI thread cannot run captured continuations.
        }
    }
}
