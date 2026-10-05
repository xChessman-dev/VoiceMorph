using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceMorph.App.Models;

namespace VoiceMorph.App.Services;

/// <summary>
/// Microphone → bounded 16 kHz chunks → external RVC worker → virtual microphone.
/// This engine never substitutes the unprocessed microphone when inference fails.
/// The runtime belongs to the caller and can also be used for file previews.
/// </summary>
public sealed class NeuralAudioEngine : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Session? _session;
    private NeuralRuntimeOptions _options = new();
    private volatile bool _bypassed;
    private int _reportedLatencyMs;
    private double _lastProcessingMs;
    private float _lastInput;
    private float _lastOutput;
    private int _droppedBlocks;

    public event EventHandler<AudioMeterEventArgs>? MeterUpdated;
    public event EventHandler<string>? EngineFaulted;
    public event EventHandler<NeuralAudioTelemetry>? TelemetryUpdated;

    public bool IsRunning => Volatile.Read(ref _session) is { } session && !session.Cancellation.IsCancellationRequested;
    public bool IsBypassed => _bypassed;
    public int ReportedLatencyMs => Volatile.Read(ref _reportedLatencyMs);
    public double LastProcessingMs => Volatile.Read(ref _lastProcessingMs);
    public int DroppedBlocks => Volatile.Read(ref _droppedBlocks);
    public int BufferedMilliseconds
    {
        get
        {
            var session = Volatile.Read(ref _session);
            if (session is null) return 0;
            return (int)Math.Round(session.CaptureBuffer.BufferedDuration.TotalMilliseconds +
                                   session.OutputBuffer.BufferedDuration.TotalMilliseconds);
        }
    }

    public async Task StartAsync(string inputDeviceId, string outputDeviceId, NeuralVoiceModel model,
        NeuralRuntimeOptions options, int blockMs, NeuralRuntimeClient client,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputDeviceId) || string.IsNullOrWhiteSpace(outputDeviceId))
            throw new InvalidOperationException("Выберите микрофон и виртуальный выход.");
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(model);
        blockMs = Math.Clamp(blockMs, 200, 1000);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        Session? created = null;
        try
        {
            if (_session is not null) return;
            var inspection = await client.LoadModelAsync(model, options, cancellationToken).ConfigureAwait(false);
            if (inspection.SampleRate is < 16000 or > 96000)
                throw new InvalidOperationException("Частота модели не поддерживается для живого аудио.");
            cancellationToken.ThrowIfCancellationRequested();

            using var enumerator = new MMDeviceEnumerator();
            using var microphone = enumerator.GetDevice(inputDeviceId);
            using var outputDevice = enumerator.GetDevice(outputDeviceId);
            WasapiRecorder? recorder = null;
            WasapiPlayer? player = null;
            try
            {
                recorder = new WasapiRecorderBuilder().WithDevice(microphone).WithSharedMode().WithEventSync().Build();
                player = new WasapiPlayerBuilder().WithDevice(outputDevice).WithSharedMode().WithEventSync()
                    .WithLatency(24).WithLowLatency().WithMmcssThreadPriority("Pro Audio").Build();
                var captureBuffer = new BufferedWaveProvider(recorder.WaveFormat,
                    TimeSpan.FromMilliseconds(Math.Max(1000, blockMs * 3)))
                { DiscardOnBufferOverflow = true, ReadFully = false };
                ISampleProvider captureSource = captureBuffer.ToSampleProvider();
                if (captureSource.WaveFormat.Channels == 2)
                    captureSource = new StereoToMonoSampleProvider(captureSource) { LeftVolume = .5f, RightVolume = .5f };
                if (captureSource.WaveFormat.Channels != 1)
                    throw new InvalidOperationException("RVC поддерживает моно- или стереомикрофон.");
                if (captureSource.WaveFormat.SampleRate != 16000)
                    captureSource = new WdlResamplingSampleProvider(captureSource, 16000);

                var outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(inspection.SampleRate, 1);
                var outputBuffer = new BufferedWaveProvider(outputFormat,
                    TimeSpan.FromMilliseconds(blockMs * 4 + 100))
                { DiscardOnBufferOverflow = false, ReadFully = true };
                var outputSource = new PrefilledOutputProvider(outputBuffer,
                    Math.Max(1, inspection.SampleRate * blockMs / 1000));
                var outputMeter = new MeteringSampleProvider(outputSource)
                { SamplesPerNotification = Math.Max(256, inspection.SampleRate / 30) };
                outputMeter.StreamVolume += OutputMeter_StreamVolume;
                ISampleProvider playback = outputMeter;
                if (player.DeviceMixFormat.Channels == 2)
                    playback = new MonoToStereoSampleProvider(playback);
                else if (player.DeviceMixFormat.Channels != 1)
                    throw new InvalidOperationException("Выберите моно- или стереовыход VB-CABLE, не 16-канальный.");
                if (playback.WaveFormat.SampleRate != player.DeviceMixFormat.SampleRate)
                    playback = new WdlResamplingSampleProvider(playback, player.DeviceMixFormat.SampleRate);
                player.Init(playback.ToWaveProvider());

                created = new Session(recorder, player, captureBuffer, captureSource, outputBuffer,
                    outputSource, outputMeter, blockMs, client, cancellationToken);
                created.Chunks = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(1)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest },
                    _ => Interlocked.Increment(ref _droppedBlocks));
                Volatile.Write(ref _options, options);
                _bypassed = false;
                Interlocked.Exchange(ref _droppedBlocks, 0);
                Volatile.Write(ref _lastProcessingMs, 0);
                Volatile.Write(ref _reportedLatencyMs, blockMs + (int)Math.Ceiling(
                    (double)recorder.LatencyMilliseconds + player.LatencyMilliseconds));
                recorder.DataAvailable += Recorder_DataAvailable;
                recorder.RecordingStopped += Recorder_RecordingStopped;
                player.PlaybackStopped += Player_PlaybackStopped;
                Volatile.Write(ref _session, created);
                created.PumpTask = Task.Run(() => PumpCaptureAsync(created));
                created.ConversionTask = Task.Run(() => ConvertChunksAsync(created));
                player.Play();
                recorder.StartRecording();
            }
            catch
            {
                if (created is null) { recorder?.Dispose(); player?.Dispose(); }
                throw;
            }
        }
        catch
        {
            if (created is not null)
            {
                Volatile.Write(ref _session, null);
                await StopSessionAsync(created).ConfigureAwait(false);
            }
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public void ApplyOptions(NeuralRuntimeOptions options) => Volatile.Write(ref _options, options);

    /// <summary>Applies at a chunk boundary, so bypass has the same buffered delay.</summary>
    public void SetBypass(bool bypassed) => _bypassed = bypassed;

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = Interlocked.Exchange(ref _session, null);
            if (session is not null) await StopSessionAsync(session).ConfigureAwait(false);
            _lastInput = _lastOutput = 0;
            Volatile.Write(ref _reportedLatencyMs, 0);
            Volatile.Write(ref _lastProcessingMs, 0);
            MeterUpdated?.Invoke(this, new AudioMeterEventArgs(0, 0));
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task StopSessionAsync(Session session)
    {
        session.Cancellation.Cancel();
        session.Chunks.Writer.TryComplete();
        session.Recorder.DataAvailable -= Recorder_DataAvailable;
        session.Recorder.RecordingStopped -= Recorder_RecordingStopped;
        session.Player.PlaybackStopped -= Player_PlaybackStopped;
        try { session.Recorder.StopRecording(); } catch { /* The device may have vanished. */ }
        try { session.Player.Stop(); } catch { /* The output may have vanished. */ }
        session.Recorder.Dispose();
        session.Player.Dispose();
        session.OutputMeter.StreamVolume -= OutputMeter_StreamVolume;
        var tasks = Task.WhenAll(session.PumpTask, session.ConversionTask);
        try { await tasks.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (TimeoutException)
        {
            // A hung external worker cannot retain any audio device. Its owner
            // can restart it; final managed cleanup happens when the call exits.
            _ = tasks.ContinueWith(_ => session.DisposeSignals(), TaskScheduler.Default);
            return;
        }
        catch (Exception) { /* The worker already reported its useful error. */ }
        session.DisposeSignals();
    }

    private void Recorder_DataAvailable(ReadOnlySpan<byte> samples, AudioClientBufferFlags flags,
        long devicePosition, long qpcPosition)
    {
        var session = Volatile.Read(ref _session);
        if (session is null || session.Cancellation.IsCancellationRequested) return;
        lock (session.CaptureSync)
        {
            // Capture overflow drops stale data, not new data. Latency stays bounded.
            if (session.CaptureBuffer.BufferedBytes + samples.Length > session.CaptureBuffer.BufferLength)
            {
                session.CaptureBuffer.ClearBuffer();
                Interlocked.Increment(ref _droppedBlocks);
            }
            session.CaptureBuffer.AddSamples(samples);
        }
        try { session.CaptureReady.Release(); }
        catch (SemaphoreFullException) { /* One wake-up is sufficient to drain available audio. */ }
        catch (ObjectDisposedException) { /* A final callback raced with device shutdown. */ }
    }

    private async Task PumpCaptureAsync(Session session)
    {
        var token = session.Cancellation.Token;
        var blockLength = 16000 * session.BlockMs / 1000;
        var chunk = new float[blockLength];
        var filled = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await session.CaptureReady.WaitAsync(token).ConfigureAwait(false);
                while (!token.IsCancellationRequested)
                {
                    int read;
                    lock (session.CaptureSync)
                        read = session.CaptureSource.Read(chunk.AsSpan(filled));
                    if (read == 0) break;
                    filled += read;
                    if (filled < blockLength) continue;
                    _lastInput = Peak(chunk);
                    MeterUpdated?.Invoke(this, new AudioMeterEventArgs(_lastInput, _lastOutput));
                    session.Chunks.Writer.TryWrite(new AudioChunk(chunk, Stopwatch.GetTimestamp()));
                    chunk = new float[blockLength];
                    filled = 0;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Fail(session, "Ошибка захвата RVC: " + exception.Message); }
        finally { session.Chunks.Writer.TryComplete(); }
    }

    private async Task ConvertChunksAsync(Session session)
    {
        var token = session.Cancellation.Token;
        try
        {
            await foreach (var chunk in session.Chunks.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var queueMs = Stopwatch.GetElapsedTime(chunk.CapturedAt).TotalMilliseconds;
                var options = Volatile.Read(ref _options);
                var watch = Stopwatch.StartNew();
                NeuralAudioResult result;
                if (_bypassed)
                    result = new NeuralAudioResult(ResampleDry(chunk.Samples, session.OutputBuffer.WaveFormat.SampleRate),
                        session.OutputBuffer.WaveFormat.SampleRate, 0);
                else
                    result = await session.Client.ConvertSamplesAsync(chunk.Samples, options, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var elapsedMs = watch.Elapsed.TotalMilliseconds;
                if (result.SampleRate != session.OutputBuffer.WaveFormat.SampleRate ||
                    result.Samples.Length == 0 ||
                    result.Samples.Length > result.SampleRate * (session.BlockMs / 1000d + .1))
                    throw new InvalidOperationException("RVC вернул несовместимый размер или частоту живого блока.");
                SanitizeOutput(result.Samples);
                var audioBytes = MemoryMarshal.AsBytes(result.Samples.AsSpan());
                double outputAheadMs;
                lock (session.OutputSync)
                {
                    // Keep at most two blocks of pending output, even if a worker
                    // suddenly catches up after a stall. This is not an endless FIFO.
                    if (session.OutputBuffer.BufferedDuration.TotalMilliseconds > session.BlockMs * 2 ||
                        session.OutputBuffer.BufferedBytes + audioBytes.Length > session.OutputBuffer.BufferLength)
                    {
                        session.OutputBuffer.ClearBuffer();
                        session.OutputSource.Reprime();
                        Interlocked.Increment(ref _droppedBlocks);
                    }
                    outputAheadMs = session.OutputBuffer.BufferedDuration.TotalMilliseconds;
                    session.OutputBuffer.AddSamples(audioBytes);
                }
                var outputQueuedMs = session.OutputBuffer.BufferedDuration.TotalMilliseconds;
                Volatile.Write(ref _lastProcessingMs, elapsedMs);
                var latency = session.BlockMs + queueMs + elapsedMs + outputAheadMs +
                              session.Recorder.LatencyMilliseconds + session.Player.LatencyMilliseconds;
                Volatile.Write(ref _reportedLatencyMs, (int)Math.Ceiling(latency));
                var lagging = elapsedMs > session.BlockMs || DroppedBlocks != session.LastReportedDrops ||
                              queueMs > session.BlockMs;
                session.LastReportedDrops = DroppedBlocks;
                TelemetryUpdated?.Invoke(this, new NeuralAudioTelemetry(session.BlockMs, elapsedMs,
                    queueMs, outputQueuedMs, ReportedLatencyMs, DroppedBlocks,
                    session.OutputSource.Underruns, lagging));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Fail(session, "RVC остановлен: " + exception.Message); }
    }

    private void Fail(Session session, string message)
    {
        if (session.Cancellation.IsCancellationRequested) return;
        session.Cancellation.Cancel();
        EngineFaulted?.Invoke(this, message);
        _ = Task.Run(StopAsync);
    }

    private void Recorder_RecordingStopped(object? sender, StoppedEventArgs args)
    {
        var session = Volatile.Read(ref _session);
        if (session is not null && args.Exception is not null)
            Fail(session, "Микрофон RVC остановлен: " + args.Exception.Message);
    }

    private void Player_PlaybackStopped(object? sender, StoppedEventArgs args)
    {
        var session = Volatile.Read(ref _session);
        if (session is not null && args.Exception is not null)
            Fail(session, "Выход RVC остановлен: " + args.Exception.Message);
    }

    private void OutputMeter_StreamVolume(object? sender, StreamVolumeEventArgs args)
    {
        _lastOutput = Peak(args.MaxSampleValues);
        MeterUpdated?.Invoke(this, new AudioMeterEventArgs(_lastInput, _lastOutput));
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var value in samples) if (float.IsFinite(value)) peak = Math.Max(peak, Math.Abs(value));
        return Math.Clamp(peak, 0, 1);
    }

    internal static void SanitizeOutput(Span<float> samples)
    {
        foreach (ref var value in samples)
            value = float.IsFinite(value) ? Math.Clamp(value, -.891f, .891f) : 0;
    }

    private static float[] ResampleDry(float[] source, int outputRate)
    {
        if (outputRate == 16000) return (float[])source.Clone();
        var length = (int)Math.Round(source.Length * outputRate / 16000d);
        var result = new float[length];
        for (var index = 0; index < length; index++)
        {
            var position = index * 16000d / outputRate;
            var first = Math.Min((int)position, source.Length - 1);
            var second = Math.Min(first + 1, source.Length - 1);
            result[index] = source[first] + (source[second] - source[first]) * (float)(position - first);
        }
        return result;
    }

    private sealed record AudioChunk(float[] Samples, long CapturedAt);

    private sealed class Session(WasapiRecorder recorder, WasapiPlayer player,
        BufferedWaveProvider captureBuffer, ISampleProvider captureSource,
        BufferedWaveProvider outputBuffer, PrefilledOutputProvider outputSource,
        MeteringSampleProvider outputMeter, int blockMs, NeuralRuntimeClient client,
        CancellationToken cancellationToken)
    {
        public WasapiRecorder Recorder { get; } = recorder;
        public WasapiPlayer Player { get; } = player;
        public BufferedWaveProvider CaptureBuffer { get; } = captureBuffer;
        public ISampleProvider CaptureSource { get; } = captureSource;
        public BufferedWaveProvider OutputBuffer { get; } = outputBuffer;
        public PrefilledOutputProvider OutputSource { get; } = outputSource;
        public MeteringSampleProvider OutputMeter { get; } = outputMeter;
        public int BlockMs { get; } = blockMs;
        public NeuralRuntimeClient Client { get; } = client;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public SemaphoreSlim CaptureReady { get; } = new(0, 1);
        public object CaptureSync { get; } = new();
        public object OutputSync { get; } = new();
        public Channel<AudioChunk> Chunks { get; set; } = null!;
        public Task PumpTask { get; set; } = Task.CompletedTask;
        public Task ConversionTask { get; set; } = Task.CompletedTask;
        public int LastReportedDrops { get; set; }
        public void DisposeSignals() { CaptureReady.Dispose(); Cancellation.Dispose(); }
    }

    /// <summary>Silence while priming or starved. No original voice can leak here.</summary>
    private sealed class PrefilledOutputProvider(BufferedWaveProvider buffer, int prefillSamples) : ISampleProvider
    {
        private readonly ISampleProvider _source = buffer.ToSampleProvider();
        private int _primed;
        private int _underruns;
        public WaveFormat WaveFormat => _source.WaveFormat;
        public int Underruns => Volatile.Read(ref _underruns);
        public void Reprime() => Volatile.Write(ref _primed, 0);
        public int Read(Span<float> samples)
        {
            if (Volatile.Read(ref _primed) == 0)
            {
                if (buffer.BufferedBytes < prefillSamples * sizeof(float))
                { samples.Clear(); return samples.Length; }
                Volatile.Write(ref _primed, 1);
            }
            if (buffer.BufferedBytes == 0)
            {
                Interlocked.Increment(ref _underruns);
                Reprime();
                samples.Clear();
                return samples.Length;
            }
            return _source.Read(samples);
        }
    }
}

public sealed record NeuralAudioTelemetry(int BlockMilliseconds, double ProcessingMilliseconds,
    double QueueMilliseconds, double OutputBufferedMilliseconds, int EstimatedLatencyMilliseconds,
    int DroppedBlocks, int Underruns, bool CannotKeepUp);
