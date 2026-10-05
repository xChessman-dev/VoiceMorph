using System.IO;
using NAudio.Wave;
using VoiceMorph.App.Models;
using VoiceMorph.App.Services;

public static class PreviewVerification
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, ".preview-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"silence-{Guid.NewGuid():N}.wav");
        // Hardware validation emits silence only. No microphone is captured and
        // no user recording is opened, modified or sent anywhere.
        using (var writer = new WaveFileWriter(path, new WaveFormat(48_000, 16, 1)))
            writer.Write(new byte[28_800]);
        using var preview = new PreviewService();
        try
        {
            await preview.PlayAsync(path, VoiceSettings.Neutral, false);
            check(!preview.IsPlaying, "Предпрослушивание исходника завершается и освобождает аудиовыход");
            var markers = new[] { "CABLE", "Voicemeeter", "VoiceBridge", "Virtual", "Loopback" };
            check(!string.IsNullOrWhiteSpace(preview.LastOutputDeviceName) &&
                  !markers.Any(m => preview.LastOutputDeviceName.Contains(m, StringComparison.OrdinalIgnoreCase)),
                $"Предпрослушивание направлено в физический выход: {preview.LastOutputDeviceName}");
            await preview.PlayAsync(path, VoiceSettings.FromPreset(VoicePreset.BuiltIns[0]), true);
            check(!preview.IsPlaying, "Обработанный вариант воспроизводится общим DSP без сохранения нового WAV");

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.CancelAfter(70);
                var cancelled = await WasCancelled(preview.PlayAsync(path, VoiceSettings.Neutral, true, cancellation.Token));
                check(cancelled && !preview.IsPlaying, "Внешняя отмена прекращает предпрослушивание и завершает задачу как отменённую");
            }

            var first = preview.PlayAsync(path, VoiceSettings.Neutral, false);
            var second = preview.PlayAsync(path, VoiceSettings.Neutral, true);
            var firstCancelled = await WasCancelled(first);
            await second;
            check(firstCancelled && !preview.IsPlaying, "Повторный запуск отменяет старый A/B-фрагмент без одновременного воспроизведения");

            var stoppable = preview.PlayAsync(path, VoiceSettings.Neutral, true);
            await Task.Delay(70);
            preview.Stop();
            preview.Stop();
            check(await WasCancelled(stoppable) && !preview.IsPlaying,
                "Повторный Stop безопасен и освобождает поток проигрывателя");

            var disposable = preview.PlayAsync(path, VoiceSettings.Neutral, false);
            await Task.Delay(70);
            preview.Dispose();
            var disposeCancelled = await WasCancelled(disposable);
            var rejected = false;
            try { await preview.PlayAsync(path, VoiceSettings.Neutral, false); }
            catch (ObjectDisposedException) { rejected = true; }
            check(disposeCancelled && rejected && !preview.IsPlaying,
                "Dispose прерывает аудио и предотвращает повторное использование освобождённого сервиса");
        }
        catch (Exception exception)
        {
            check(false, $"Аппаратная проверка предпрослушивания: {exception.Message}");
        }
        finally
        {
            preview.Stop();
            File.Delete(path);
        }
    }

    private static async Task<bool> WasCancelled(Task task)
    {
        try { await task; return false; }
        catch (OperationCanceledException) { return true; }
    }
}
