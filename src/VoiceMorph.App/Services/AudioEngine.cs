using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceMorph.App.Models;
using VoiceMorph.App.Services.Dsp;

namespace VoiceMorph.App.Services;

public sealed class AudioEngine : IDisposable
{
    private readonly object _sync = new();
    private WasapiRecorder? _recorder;
    private WasapiPlayer? _player;
    private BufferedWaveProvider? _captureBuffer;
    private VoiceDspProcessor? _processor;
    private MeteringSampleProvider? _inputMeter;
    private MeteringSampleProvider? _outputMeter;
    private VoiceSettings _settings = VoiceSettings.FromPreset(VoicePreset.BuiltIns[0]);
    private volatile bool _bypassed;
    private float _lastInput;
    private float _lastOutput;
    private int _reportedLatencyMs;

    public event EventHandler<AudioMeterEventArgs>? MeterUpdated;
    public event EventHandler<string>? EngineFaulted;

    public bool IsRunning
    {
        get { lock (_sync) return _recorder is not null && _player is not null; }
    }

    public bool IsBypassed => _bypassed;
    public int ReportedLatencyMs => Volatile.Read(ref _reportedLatencyMs);

    public int BufferedMilliseconds
    {
        get
        {
            lock (_sync)
            {
                if (_captureBuffer is null || _captureBuffer.WaveFormat.AverageBytesPerSecond == 0) return 0;
                return (int)Math.Round(_captureBuffer.BufferedBytes * 1000d /
                                       _captureBuffer.WaveFormat.AverageBytesPerSecond);
            }
        }
    }

    public static IReadOnlyList<AudioDeviceInfo> GetInputDevices() => GetDevices(DataFlow.Capture);
    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => GetDevices(DataFlow.Render);

    /// <summary>The same chain is used for live output and offline reference previews.</summary>
    public static VoiceDspProcessor CreateProcessingPipeline(ISampleProvider source, VoiceSettings settings) =>
        new(source, settings);

    private static IReadOnlyList<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var result = new List<AudioDeviceInfo>();
        foreach (var device in devices)
        {
            using (device) result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName.Trim()));
        }
        return result;
    }

    public Task StartAsync(string inputDeviceId, string outputDeviceId)
    {
        if (string.IsNullOrWhiteSpace(inputDeviceId) || string.IsNullOrWhiteSpace(outputDeviceId))
            throw new InvalidOperationException("Выберите микрофон и виртуальный выход.");

        lock (_sync)
        {
            if (_recorder is not null || _player is not null) return Task.CompletedTask;
        }

        using var enumerator = new MMDeviceEnumerator();
        using var inputDevice = enumerator.GetDevice(inputDeviceId);
        using var outputDevice = enumerator.GetDevice(outputDeviceId);
        WasapiRecorder? recorder = null;
        WasapiPlayer? player = null;
        try
        {
            recorder = new WasapiRecorderBuilder().WithDevice(inputDevice).WithSharedMode().WithEventSync().Build();
            player = new WasapiPlayerBuilder().WithDevice(outputDevice).WithSharedMode().WithEventSync()
                .WithLatency(24).WithLowLatency().WithMmcssThreadPriority("Pro Audio").Build();
            var captureBuffer = new BufferedWaveProvider(recorder.WaveFormat, TimeSpan.FromMilliseconds(180))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            ISampleProvider source = captureBuffer.ToSampleProvider();
            // Microphones exposed as stereo are downmixed once. Voice processing
            // is mono, avoids duplicate FFT work and produces a centred output.
            if (source.WaveFormat.Channels == 2)
                source = new StereoToMonoSampleProvider(source) { LeftVolume = .5f, RightVolume = .5f };
            if (source.WaveFormat.Channels != 1)
                throw new InvalidOperationException("Выберите моно- или стереомикрофон.");
            var inputMeter = new MeteringSampleProvider(source)
            {
                SamplesPerNotification = Math.Max(256, source.WaveFormat.SampleRate / 30),
            };
            inputMeter.StreamVolume += InputMeter_StreamVolume;
            var processor = CreateProcessingPipeline(inputMeter, _settings);
            processor.SetBypass(_bypassed);
            var outputMeter = new MeteringSampleProvider(processor)
            {
                SamplesPerNotification = Math.Max(256, processor.WaveFormat.SampleRate / 30),
            };
            outputMeter.StreamVolume += OutputMeter_StreamVolume;

            ISampleProvider output = AdaptChannels(outputMeter, player.DeviceMixFormat.Channels);
            if (output.WaveFormat.SampleRate != player.DeviceMixFormat.SampleRate)
                output = new WdlResamplingSampleProvider(output, player.DeviceMixFormat.SampleRate);
            player.Init(output.ToWaveProvider());
            recorder.DataAvailable += Recorder_DataAvailable;
            recorder.RecordingStopped += Recorder_RecordingStopped;
            player.PlaybackStopped += Player_PlaybackStopped;
            lock (_sync)
            {
                _recorder = recorder;
                _player = player;
                _captureBuffer = captureBuffer;
                _processor = processor;
                _inputMeter = inputMeter;
                _outputMeter = outputMeter;
                _reportedLatencyMs = (int)Math.Round(recorder.LatencyMilliseconds + player.LatencyMilliseconds +
                    processor.LatencySamples * 1000d / processor.WaveFormat.SampleRate);
                processor.SetSettings(_settings);
                processor.SetBypass(_bypassed);
            }
            try { player.Play(); }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Не удалось открыть выход «{outputDevice.FriendlyName}»: {exception.Message}", exception);
            }
            try { recorder.StartRecording(); }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Не удалось открыть микрофон «{inputDevice.FriendlyName}»: {exception.Message}", exception);
            }
        }
        catch
        {
            if (ReferenceEquals(_recorder, recorder)) Stop();
            else { recorder?.Dispose(); player?.Dispose(); }
            throw;
        }
        return Task.CompletedTask;
    }

    public void ApplySettings(VoiceSettings settings)
    {
        lock (_sync)
        {
            _settings = settings.Sanitize();
            _processor?.SetSettings(_settings);
        }
    }

    public void SetBypass(bool bypassed)
    {
        lock (_sync)
        {
            _bypassed = bypassed;
            _processor?.SetBypass(bypassed);
        }
    }

    public void Stop()
    {
        WasapiRecorder? recorder;
        WasapiPlayer? player;
        MeteringSampleProvider? inputMeter;
        MeteringSampleProvider? outputMeter;
        lock (_sync)
        {
            recorder = _recorder; player = _player;
            inputMeter = _inputMeter; outputMeter = _outputMeter;
            _recorder = null; _player = null; _captureBuffer = null; _processor = null;
            _inputMeter = null; _outputMeter = null; _reportedLatencyMs = 0;
        }
        if (recorder is not null)
        {
            recorder.DataAvailable -= Recorder_DataAvailable;
            recorder.RecordingStopped -= Recorder_RecordingStopped;
            try { recorder.StopRecording(); } catch { /* Device may already be gone. */ }
            recorder.Dispose();
        }
        if (player is not null)
        {
            player.PlaybackStopped -= Player_PlaybackStopped;
            try { player.Stop(); } catch { /* Device may already be gone. */ }
            player.Dispose();
        }
        if (inputMeter is not null) inputMeter.StreamVolume -= InputMeter_StreamVolume;
        if (outputMeter is not null) outputMeter.StreamVolume -= OutputMeter_StreamVolume;
        _lastInput = 0; _lastOutput = 0;
        MeterUpdated?.Invoke(this, new AudioMeterEventArgs(0, 0));
    }

    public void Dispose() { Stop(); GC.SuppressFinalize(this); }

    private void Recorder_DataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        lock (_sync) _captureBuffer?.AddSamples(buffer);
    }

    private void Recorder_RecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null)
            EngineFaulted?.Invoke(this, $"Микрофон остановлен: {args.Exception.Message}. Выберите устройство и перезапустите обработку.");
    }

    private void Player_PlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null)
            EngineFaulted?.Invoke(this, $"Аудиовыход остановлен: {args.Exception.Message}. Проверьте VB-CABLE.");
    }

    private void InputMeter_StreamVolume(object? sender, StreamVolumeEventArgs args)
    {
        _lastInput = Peak(args.MaxSampleValues); RaiseMeter();
    }

    private void OutputMeter_StreamVolume(object? sender, StreamVolumeEventArgs args)
    {
        _lastOutput = Peak(args.MaxSampleValues); RaiseMeter();
    }

    private void RaiseMeter() => MeterUpdated?.Invoke(this, new AudioMeterEventArgs(_lastInput, _lastOutput));

    private static ISampleProvider AdaptChannels(ISampleProvider source, int channels)
    {
        if (source.WaveFormat.Channels == channels) return source;
        if (source.WaveFormat.Channels == 1 && channels == 2) return new MonoToStereoSampleProvider(source);
        if (source.WaveFormat.Channels == 2 && channels == 1) return new StereoToMonoSampleProvider(source);
        throw new InvalidOperationException($"Не поддерживается преобразование {source.WaveFormat.Channels} → {channels} каналов.");
    }

    private static float Peak(IEnumerable<float> values) => values.Where(float.IsFinite).Select(Math.Abs).DefaultIfEmpty(0).Max();
}
