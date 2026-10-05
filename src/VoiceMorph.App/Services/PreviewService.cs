using System.IO;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceMorph.App.Models;

namespace VoiceMorph.App.Services;

/// <summary>
/// Plays the same first ten seconds of calibration through physical speakers.
/// Audio is streamed in memory; no rendered audio or model files are written.
/// </summary>
public sealed class PreviewService : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _playGate = new(1, 1);
    private CancellationTokenSource? _activeCancellation;
    private bool _disposed;

    public bool IsPlaying { get { lock (_sync) return _activeCancellation is not null; } }
    public string? LastOutputDeviceName { get; private set; }

    public async Task PlayAsync(string path, VoiceSettings settings, bool processed,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource operation;
        CancellationTokenSource? previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _activeCancellation;
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = operation;
        }
        CancelSafely(previous);
        var entered = false;
        try
        {
            await _playGate.WaitAsync(operation.Token).ConfigureAwait(false);
            entered = true;
            operation.Token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) throw new FileNotFoundException("Запись вашего голоса не найдена.", path);

            using var reader = new AudioFileReader(path);
            ISampleProvider source = reader;
            if (source.WaveFormat.Channels == 2)
                source = new StereoToMonoSampleProvider(source) { LeftVolume = .5f, RightVolume = .5f };
            if (source.WaveFormat.Channels != 1)
                throw new InvalidDataException("Для предпрослушивания нужна моно- или стереозапись голоса.");
            // Extra silence flushes the spectral processor's last audio frame.
            source = new OffsetSampleProvider(source)
            {
                Take = TimeSpan.FromSeconds(10),
                LeadOut = TimeSpan.FromMilliseconds(120),
            };
            if (processed) source = AudioEngine.CreateProcessingPipeline(source, settings);
            source = new SafePreviewSamples(source);

            using var enumerator = new MMDeviceEnumerator();
            using var outputDevice = FindPhysicalOutput(enumerator);
            LastOutputDeviceName = outputDevice.FriendlyName;
            using var player = new WasapiPlayerBuilder().WithDevice(outputDevice).WithSharedMode()
                .WithEventSync().WithLatency(60).Build();
            if (source.WaveFormat.Channels == 1 && player.DeviceMixFormat.Channels == 2)
                source = new MonoToStereoSampleProvider(source);
            else if (source.WaveFormat.Channels == 1 && player.DeviceMixFormat.Channels is > 2 and <= 8)
                source = new FrontChannelSamples(source, player.DeviceMixFormat.Channels);
            if (source.WaveFormat.Channels != player.DeviceMixFormat.Channels)
                throw new InvalidOperationException("Формат выбранного аудиовыхода не поддерживается. Выберите обычные наушники или колонки в Windows.");
            if (source.WaveFormat.SampleRate != player.DeviceMixFormat.SampleRate)
                source = new WdlResamplingSampleProvider(source, player.DeviceMixFormat.SampleRate);
            // 7.1 USB headsets may accept only their exact extensible format,
            // including speaker mask. Preserve that format rather than supplying
            // an ordinary float WAVEFORMATEX with the same channel count.
            player.Init(new NativeOutputWaveProvider(source, player.DeviceMixFormat));

            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var playSync = new object();
            void OnStopped(object? sender, StoppedEventArgs args)
            {
                if (args.Exception is not null) completed.TrySetException(args.Exception);
                else completed.TrySetResult();
            }
            player.PlaybackStopped += OnStopped;
            try
            {
                using var stopRegistration = operation.Token.Register(() =>
                {
                    // Mark cancellation before Stop raises PlaybackStopped;
                    // otherwise an interrupted preview can look like success.
                    completed.TrySetCanceled(operation.Token);
                    lock (playSync)
                    {
                        try { player.Stop(); } catch { /* Endpoint may have disconnected. */ }
                    }
                });
                lock (playSync)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    player.Play();
                }
                // A disconnected endpoint must not leave the UI awaiting forever.
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(14), operation.Token).ConfigureAwait(false);
            }
            finally
            {
                player.PlaybackStopped -= OnStopped;
                lock (playSync)
                {
                    try { player.Stop(); } catch { /* Dispose still releases the endpoint. */ }
                }
            }
        }
        finally
        {
            if (entered) _playGate.Release();
            lock (_sync)
            {
                if (ReferenceEquals(_activeCancellation, operation)) _activeCancellation = null;
            }
            operation.Dispose();
        }
    }

    public void Stop()
    {
        CancellationTokenSource? operation;
        lock (_sync) operation = _activeCancellation;
        CancelSafely(operation);
    }

    public void Dispose()
    {
        CancellationTokenSource? operation;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            operation = _activeCancellation;
        }
        CancelSafely(operation);
        GC.SuppressFinalize(this);
    }

    private static void CancelSafely(CancellationTokenSource? operation)
    {
        try { operation?.Cancel(); } catch (ObjectDisposedException) { /* Playback just completed. */ }
    }

    private static MMDevice FindPhysicalOutput(MMDeviceEnumerator enumerator)
    {
        MMDevice? primary = null;
        try { primary = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch { /* Try another active physical endpoint below. */ }
        if (primary is not null && !IsVirtual(primary.FriendlyName)) return primary;
        primary?.Dispose();
        MMDevice? selected = null;
        foreach (var candidate in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (selected is null && !IsVirtual(candidate.FriendlyName)) selected = candidate;
            else candidate.Dispose();
        }
        return selected ?? throw new InvalidOperationException("Не найдены физические наушники или колонки. Предпрослушивание не направляется в VB-CABLE.");
    }

    private static bool IsVirtual(string name) => new[]
    {
        "CABLE", "Voicemeeter", "VoiceBridge", "Virtual", "Loopback",
    }.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private sealed class SafePreviewSamples(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(Span<float> buffer)
        {
            var count = source.Read(buffer);
            for (var i = 0; i < count; i++)
                buffer[i] = float.IsFinite(buffer[i]) ? Math.Clamp(buffer[i], -.97f, .97f) * .85f : 0;
            return count;
        }
    }

    private sealed class FrontChannelSamples(ISampleProvider source, int channels) : ISampleProvider
    {
        private float[] _mono = new float[1024];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);

        public int Read(Span<float> buffer)
        {
            var frames = buffer.Length / channels;
            if (_mono.Length < frames) _mono = new float[frames];
            var read = source.Read(_mono.AsSpan(0, frames));
            var count = read * channels;
            buffer[..count].Clear();
            for (var frame = 0; frame < read; frame++)
            {
                // WASAPI channel order starts with front left/front right.
                // Surround, centre and LFE remain silent for a mono voice preview.
                buffer[frame * channels] = _mono[frame];
                buffer[frame * channels + 1] = _mono[frame];
            }
            return count;
        }
    }

    private sealed class NativeOutputWaveProvider : IWaveProvider
    {
        private readonly ISampleProvider _source;
        private readonly WaveFormat _standard;
        private float[] _samples = new float[4096];

        public NativeOutputWaveProvider(ISampleProvider source, WaveFormat format)
        {
            _source = source;
            WaveFormat = format;
            _standard = format.AsStandardWaveFormat();
            if (!(_standard.Encoding == WaveFormatEncoding.IeeeFloat && _standard.BitsPerSample == 32) &&
                !(_standard.Encoding == WaveFormatEncoding.Pcm && _standard.BitsPerSample is 16 or 24 or 32))
                throw new InvalidOperationException("Формат физического аудиовыхода не поддерживается.");
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<byte> buffer)
        {
            var sampleCount = buffer.Length / WaveFormat.BlockAlign * WaveFormat.Channels;
            if (_samples.Length < sampleCount) _samples = new float[sampleCount];
            var read = _source.Read(_samples.AsSpan(0, sampleCount));
            var bytesPerSample = _standard.BitsPerSample / 8;
            if (_standard.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                _samples.AsSpan(0, read).CopyTo(MemoryMarshal.Cast<byte, float>(buffer[..(read * 4)]));
                return read * 4;
            }
            for (var i = 0; i < read; i++)
            {
                var value = Math.Clamp(_samples[i], -.999f, .999f);
                var destination = buffer.Slice(i * bytesPerSample, bytesPerSample);
                if (bytesPerSample == 2)
                    BinaryPrimitives.WriteInt16LittleEndian(destination, (short)(value * 32767));
                else if (bytesPerSample == 3)
                {
                    var integer = (int)(value * 8388607);
                    destination[0] = (byte)integer;
                    destination[1] = (byte)(integer >> 8);
                    destination[2] = (byte)(integer >> 16);
                }
                else BinaryPrimitives.WriteInt32LittleEndian(destination, (int)(value * 2147483647f));
            }
            return read * bytesPerSample;
        }
    }
}
