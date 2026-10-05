using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceMorph.App.Services;

/// <summary>A finite microphone calibration capture, with cancellation and atomic completion.</summary>
public sealed class CalibrationRecorder
{
    public async Task<string> CaptureAsync(string deviceId, string path, TimeSpan duration,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new InvalidOperationException("Выберите микрофон для калибровки.");
        if (duration.TotalSeconds is < 20 or > 90) throw new ArgumentOutOfRangeException(nameof(duration));
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".calibration-" + Guid.NewGuid().ToString("N") + ".wav");
        var sync = new object();
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? captureFailure = null;
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);
        using var recorder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithEventSync().Build();
        WaveFileWriter? writer = null;
        FileStream? outputFile = null;
        var recordingStarted = false;
        long capturedBytes = 0;
        if (recorder.WaveFormat.Channels is < 1 or > 8 ||
            recorder.WaveFormat.SampleRate is < 8000 or > 192000 ||
            recorder.WaveFormat.BitsPerSample is < 8 or > 32 ||
            recorder.WaveFormat.AverageBytesPerSecond is <= 0 or > 6_144_000)
            throw new InvalidDataException("Формат микрофона не поддерживается для калибровки.");
        var maximumBytes = (long)Math.Ceiling((duration.TotalSeconds + 1) * recorder.WaveFormat.AverageBytesPerSecond);

        void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long position, long qpc)
        {
            lock (sync)
            {
                if (writer is null || captureFailure is not null) return;
                try
                {
                    if (capturedBytes + buffer.Length > maximumBytes)
                        throw new InvalidDataException("Микрофон передал слишком большой объём данных калибровки.");
                    writer.Write(buffer);
                    capturedBytes += buffer.Length;
                }
                catch (Exception exception) { captureFailure = exception; }
            }
        }

        void OnStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception is not null) stopped.TrySetException(args.Exception);
            else stopped.TrySetResult(true);
        }

        recorder.DataAvailable += OnData;
        recorder.RecordingStopped += OnStopped;
        try
        {
            outputFile = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
            // NAudio's stream constructor leaves the stream open after finalizing the WAV header.
            writer = new WaveFileWriter(outputFile, recorder.WaveFormat);
            recorder.StartRecording();
            recordingStarted = true;
            var clock = Stopwatch.StartNew();
            progress?.Report(0);
            while (clock.Elapsed < duration)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                if (stopped.Task.IsCompleted)
                {
                    await stopped.Task.ConfigureAwait(false);
                    throw new IOException("Микрофон остановился до завершения калибровки.");
                }
                lock (sync)
                {
                    if (captureFailure is not null) throw new IOException("Не удалось сохранить калибровку.", captureFailure);
                }
                progress?.Report(Math.Clamp(clock.Elapsed.TotalSeconds / duration.TotalSeconds, 0, 1));
            }
            recorder.StopRecording();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            recordingStarted = false;
            lock (sync)
            {
                if (captureFailure is not null) throw new IOException("Не удалось сохранить калибровку.", captureFailure);
                if (capturedBytes < recorder.WaveFormat.AverageBytesPerSecond * 10L)
                    throw new IOException("Микрофон передал недостаточно данных для калибровки.");
                var completeWriter = writer ?? throw new IOException("Файл калибровки неожиданно закрыт.");
                writer = null;
                completeWriter.Dispose();
            }
            outputFile.Flush(flushToDisk: true);
            outputFile.Dispose();
            outputFile = null;
            // The completed recording replaces the previous file only at this commit point.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(1);
            return destination;
        }
        finally
        {
            if (recordingStarted)
            {
                try { recorder.StopRecording(); } catch { /* A disconnected device is already stopped. */ }
            }
            recorder.DataAvailable -= OnData;
            recorder.RecordingStopped -= OnStopped;
            lock (sync)
            {
                var incompleteWriter = writer;
                writer = null;
                try { incompleteWriter?.Dispose(); }
                catch (IOException) { /* Preserve cancellation or the original capture failure. */ }
                catch (UnauthorizedAccessException) { /* Preserve the original capture failure. */ }
            }
            try { outputFile?.Dispose(); }
            catch (IOException) { /* The unique incomplete file remains uncommitted. */ }
            if (stopped.Task.IsFaulted) _ = stopped.Task.Exception;
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* Preserve the original result; a leftover temp is never used as calibration. */ }
            catch (UnauthorizedAccessException) { /* Preserve the original result. */ }
        }
    }
}
