using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceMorph.App.Models;

namespace VoiceMorph.App.Services;

/// <summary>
/// Offline acoustic measurements. Only bounded frame buffers and scalar observations are retained;
/// reference audio is never needed in the live processing path.
/// </summary>
public sealed class VoiceAnalyzer
{
    private const int SampleRate = 16000;
    private const int FrameSize = 2048;
    private const int HopSize = 1600;
    private const double MaximumDuration = 30 * 60;

    public Task<VoiceAnalysis> AnalyzeFileAsync(
        string path,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
                throw new FileNotFoundException("Аудиофайл не найден.", path);
            if (new FileInfo(path).Length > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Выберите аудиофайл меньше 2 ГБ.");

            using var reader = new AudioFileReader(path);
            if (reader.TotalTime.TotalSeconds is <= 0 or > MaximumDuration)
                throw new InvalidDataException("Для анализа нужен аудиофайл длительностью до 30 минут.");
            if (reader.WaveFormat.Channels is < 1 or > 32 || reader.WaveFormat.SampleRate is < 8000 or > 192000)
                throw new InvalidDataException("Частота дискретизации или число каналов не поддерживаются.");
            ISampleProvider provider = reader;
            if (provider.WaveFormat.Channels != 1)
                provider = new MonoAverageProvider(provider);
            if (provider.WaveFormat.SampleRate != SampleRate)
                provider = new WdlResamplingSampleProvider(provider, SampleRate);
            return AnalyzeProvider(provider, reader.TotalTime.TotalSeconds, progress, cancellationToken);
        }, cancellationToken);
    }

    public VoiceAnalysis AnalyzeSamples(float[] samples, int sampleRate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate is < 8000 or > 192000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (samples.Length < sampleRate / 2 || samples.Length / (double)sampleRate > MaximumDuration)
            throw new InvalidDataException("Недостаточная или слишком большая длина аудио.");
        ISampleProvider provider = new ArrayProvider(samples, sampleRate);
        if (sampleRate != SampleRate)
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        return AnalyzeProvider(provider, samples.Length / (double)sampleRate, null, cancellationToken);
    }

    public VoiceProfileFit FitProfile(VoiceAnalysis source, VoiceAnalysis target, float strength = 0.75f)
    {
        ValidateMeasurements(source);
        ValidateMeasurements(target);
        if (source.DurationSeconds < 20 || source.VoicedSeconds < 8)
            throw new InvalidDataException("Сначала запишите свой голос: 30 секунд обычной речи, не менее 8 секунд с определяемой высотой.");
        if (target.DurationSeconds < 120)
            throw new InvalidDataException("Пример целевого голоса должен длиться минимум 2 минуты.");
        if (target.VoicedSeconds < 20 || target.PitchConfidence < 0.65)
            throw new InvalidDataException("В примере недостаточно чистого голоса. Нужны речь одного человека без музыки и длинных пауз.");
        if (source.ClippedSampleRatio > 0.01 || target.ClippedSampleRatio > 0.01)
            throw new InvalidDataException("Запись перегружена. Уменьшите усиление и загрузите пример без сильных искажений.");

        var notes = new List<string>
        {
            "Профиль приближает высоту и спектральный баланс. Произношение и актёрская манера остаются вашими.",
            "Резонансы оценены по общему спектру; микрофон и состав фраз также влияют на результат.",
        };
        var pitch = 12 * Math.Log2(target.MedianPitchHz / source.MedianPitchHz);
        if (Math.Abs(pitch) > 3)
            notes.Add("Большая разница высоты ограничена до 3 полутонов для уменьшения артефактов.");
        var centroidRatio = target.SpectralCentroidHz / Math.Max(80, source.SpectralCentroidHz);
        var formant = Math.Clamp(12 * Math.Log2(Math.Max(0.25, centroidRatio)) - pitch * 0.3, -1.8, 1.8);

        float BandDifference(int band) => (float)Math.Clamp(
            10 * Math.Log10((target.BandEnergyFractions[band] + 0.01) /
                           (source.BandEnergyFractions[band] + 0.01)), -3, 3);

        var settings = VoiceSettings.Neutral with
        {
            PitchSemitones = (float)Math.Clamp(pitch, -3, 3),
            FormantSemitones = (float)formant,
            FormantPreservation = 1,
            Strength = float.IsFinite(strength) ? Math.Clamp(strength, 0, 1) : 0.75f,
            BassDb = BandDifference(0),
            WarmthDb = BandDifference(1),
            MudDb = 0,
            PresenceDb = BandDifference(3),
            AirDb = BandDifference(4),
            Tone = (float)Math.Clamp(Math.Log2(Math.Max(0.25, centroidRatio)) * 0.25, -0.2, 0.2),
            Compression = (float)Math.Clamp((source.DynamicRangeDb - target.DynamicRangeDb) / 18 + 0.18, 0.05, 0.45),
            DriveDb = 0,
            ShadowMix = 0,
            RoomMix = 0,
            OutputGainDb = 0,
        };
        notes.AddRange(source.Warnings.Select(note => "Калибровка: " + note));
        notes.AddRange(target.Warnings.Select(note => "Пример: " + note));
        return new VoiceProfileFit(settings.Sanitize(), notes.Distinct().ToArray());
    }

    public static void ValidateMeasurements(VoiceAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        double[] values = [analysis.DurationSeconds, analysis.VoicedSeconds, analysis.MedianPitchHz,
            analysis.PitchP10Hz, analysis.PitchP90Hz, analysis.PitchConfidence, analysis.AverageRmsDb,
            analysis.DynamicRangeDb, analysis.PeakDb, analysis.ClippedSampleRatio, analysis.SpectralCentroidHz];
        if (values.Any(value => !double.IsFinite(value)) || analysis.DurationSeconds is <= 0 or > MaximumDuration ||
            analysis.VoicedSeconds < 0 || analysis.VoicedSeconds > analysis.DurationSeconds + 0.15 ||
            analysis.MedianPitchHz is < 60 or > 505 || analysis.PitchP10Hz < 60 || analysis.PitchP90Hz > 505 ||
            analysis.PitchP10Hz > analysis.MedianPitchHz || analysis.PitchP90Hz < analysis.MedianPitchHz ||
            analysis.PitchConfidence is < 0 or > 1 || analysis.AverageRmsDb is < -120 or > 12 ||
            analysis.PeakDb is < -120 or > 12 || analysis.DynamicRangeDb is < 0 or > 120 ||
            analysis.SpectralCentroidHz is < 80 or > 7500 ||
            analysis.ClippedSampleRatio is < 0 or > 1 || analysis.BandEnergyFractions is not { Length: 5 } ||
            analysis.BandEnergyFractions.Any(value => !double.IsFinite(value) || value < 0 || value > 1) ||
            Math.Abs(analysis.BandEnergyFractions.Sum() - 1) > 0.01 || analysis.Warnings is null ||
            analysis.Warnings.Count > 20 || analysis.Warnings.Any(value => value is null || value.Length > 600))
            throw new InvalidDataException("Некорректные акустические измерения профиля.");
    }

    private static VoiceAnalysis AnalyzeProvider(ISampleProvider provider, double advertisedDuration,
        IProgress<AnalysisProgress>? progress, CancellationToken cancellationToken)
    {
        var frame = new float[FrameSize];
        var acc = new Accumulator();
        var pitchFrame = new double[FrameSize / 2];
        var differences = new double[132];
        var real = new double[FrameSize];
        var imaginary = new double[FrameSize];
        var initial = ReadFully(provider, frame, cancellationToken);
        var totalSamples = (long)initial;
        var loaded = initial;
        var processedFrames = 0;
        if (initial == 0) throw new InvalidDataException("Аудиофайл пуст.");
        acc.CountSamples(frame.AsSpan(0, initial));
        while (loaded >= FrameSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            acc.AddFrame(frame, pitchFrame, differences, real, imaginary);
            processedFrames++;
            if (processedFrames % 8 == 0)
                progress?.Report(new(Math.Clamp(totalSamples / (SampleRate * advertisedDuration), 0, 0.99),
                    $"Анализ голоса · {totalSamples / SampleRate} / {advertisedDuration:F0} с"));
            Array.Copy(frame, HopSize, frame, 0, FrameSize - HopSize);
            var read = ReadFully(provider, frame.AsSpan(FrameSize - HopSize, HopSize), cancellationToken);
            acc.CountSamples(frame.AsSpan(FrameSize - HopSize, read));
            totalSamples += read;
            loaded = FrameSize - HopSize + read;
            if (totalSamples > SampleRate * MaximumDuration)
                throw new InvalidDataException("Аудиофайл превышает 30 минут.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = acc.Complete(totalSamples / (double)SampleRate);
        progress?.Report(new(1, "Акустический анализ завершён"));
        return result;
    }

    private static int ReadFully(ISampleProvider provider, Span<float> buffer, CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = provider.Read(buffer[total..]);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    private sealed class Accumulator
    {
        private readonly List<double> _pitch = [];
        private readonly List<double> _levels = [];
        private readonly double[] _bands = new double[5];
        private double _confidence, _spectralWeighted, _spectralPower, _squareSum, _peak;
        private long _samples, _clipped;

        public void CountSamples(ReadOnlySpan<float> samples)
        {
            foreach (var sample in samples)
            {
                if (!float.IsFinite(sample)) throw new InvalidDataException("Аудио содержит некорректные значения.");
                _samples++;
                _squareSum += sample * (double)sample;
                _peak = Math.Max(_peak, Math.Abs(sample));
                if (Math.Abs(sample) >= 0.999f) _clipped++;
            }
        }

        public void AddFrame(float[] frame, double[] pitchFrame, double[] differences,
            double[] real, double[] imaginary)
        {
            var average = frame.Average(value => (double)value);
            double energy = 0;
            for (var i = 0; i < frame.Length; i++)
                energy += Math.Pow(frame[i] - average, 2);
            var rms = Math.Sqrt(energy / frame.Length);
            var level = Db(rms);
            if (level < -48) return;
            for (var i = 0; i < pitchFrame.Length; i++)
                pitchFrame[i] = (frame[i * 2] + frame[i * 2 + 1]) * 0.5 - average;
            var (frequency, confidence) = Pitch(pitchFrame, differences);
            if (frequency == 0) return;
            _pitch.Add(frequency);
            _levels.Add(level);
            _confidence += confidence;
            Array.Clear(imaginary);
            for (var i = 0; i < frame.Length; i++)
                real[i] = (frame[i] - average) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (frame.Length - 1)));
            Fft(real, imaginary);
            for (var bin = 1; bin < FrameSize / 2; bin++)
            {
                var hz = bin * (double)SampleRate / FrameSize;
                if (hz < 80 || hz > 7500) continue;
                var power = real[bin] * real[bin] + imaginary[bin] * imaginary[bin];
                _spectralPower += power;
                _spectralWeighted += power * hz;
                var band = hz < 250 ? 0 : hz < 700 ? 1 : hz < 1800 ? 2 : hz < 4000 ? 3 : 4;
                _bands[band] += power;
            }
        }

        public VoiceAnalysis Complete(double duration)
        {
            if (_pitch.Count < 3 || _squareSum / Math.Max(1, _samples) < 1e-8)
                throw new InvalidDataException("Не найден устойчивый голос. Выберите чистую запись речи без тишины и музыки.");
            _pitch.Sort();
            _levels.Sort();
            var voiced = Math.Min(duration, _pitch.Count * HopSize / (double)SampleRate);
            var warnings = new List<string>();
            if (voiced / duration < 0.3) warnings.Add("Много пауз или непериодичного звука; точность профиля снижена.");
            if (_clipped / (double)Math.Max(1, _samples) > 0.001) warnings.Add("Есть перегруженные отсчёты.");
            if (_confidence / _pitch.Count < 0.8) warnings.Add("Высота голоса определяется нестабильно; возможны шум, музыка или несколько голосов.");
            var rmsDb = Db(Math.Sqrt(_squareSum / Math.Max(1, _samples)));
            if (rmsDb < -32) warnings.Add("Низкая громкость записи; лучше подготовить более близкую запись микрофона.");
            warnings.Add("Музыка, акцент, артикуляция и темп автоматически не моделируются; используйте речь одного человека без фоновых звуков.");
            var sum = Math.Max(_bands.Sum(), 1e-20);
            return new(duration, voiced, Percentile(_pitch, 0.5), Percentile(_pitch, 0.1),
                Percentile(_pitch, 0.9), _confidence / _pitch.Count, rmsDb,
                Percentile(_levels, 0.9) - Percentile(_levels, 0.1), Db(_peak),
                _clipped / (double)Math.Max(1, _samples), _spectralWeighted / Math.Max(_spectralPower, 1e-20),
                _bands.Select(value => value / sum).ToArray(), warnings);
        }
    }

    private static (double Frequency, double Confidence) Pitch(double[] samples, double[] cmnd)
    {
        const int minimumLag = 16;
        const int maximumLag = 123;
        var window = samples.Length - maximumLag;
        double accumulated = 0;
        cmnd[0] = 1;
        for (var lag = 1; lag <= maximumLag; lag++)
        {
            double difference = 0;
            for (var i = 0; i < window; i++)
            {
                var delta = samples[i] - samples[i + lag];
                difference += delta * delta;
            }
            accumulated += difference;
            cmnd[lag] = accumulated > 1e-20 ? difference * lag / accumulated : 1;
        }
        var best = -1;
        for (var lag = minimumLag; lag < maximumLag; lag++)
        {
            if (cmnd[lag] < 0.18)
            {
                while (lag < maximumLag && cmnd[lag + 1] < cmnd[lag]) lag++;
                best = lag;
                break;
            }
        }
        if (best < 0) return (0, 0);
        var refined = (double)best;
        if (best > 1 && best < maximumLag)
        {
            var denominator = cmnd[best - 1] - 2 * cmnd[best] + cmnd[best + 1];
            if (Math.Abs(denominator) > 1e-12)
                refined += Math.Clamp((cmnd[best - 1] - cmnd[best + 1]) / (2 * denominator), -0.5, 0.5);
        }
        return (8000 / refined, Math.Clamp(1 - cmnd[best], 0, 1));
    }

    private static void Fft(double[] real, double[] imaginary)
    {
        var length = real.Length;
        for (int i = 1, j = 0; i < length; i++)
        {
            var bit = length >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }
        for (var step = 2; step <= length; step <<= 1)
        {
            var angle = -2 * Math.PI / step;
            for (var block = 0; block < length; block += step)
            {
                double cosine = 1, sine = 0;
                var incrementCos = Math.Cos(angle);
                var incrementSin = Math.Sin(angle);
                for (var i = 0; i < step / 2; i++)
                {
                    var even = block + i;
                    var odd = even + step / 2;
                    var transformedReal = real[odd] * cosine - imaginary[odd] * sine;
                    var transformedImaginary = real[odd] * sine + imaginary[odd] * cosine;
                    real[odd] = real[even] - transformedReal;
                    imaginary[odd] = imaginary[even] - transformedImaginary;
                    real[even] += transformedReal;
                    imaginary[even] += transformedImaginary;
                    (cosine, sine) = (cosine * incrementCos - sine * incrementSin, sine * incrementCos + cosine * incrementSin);
                }
            }
        }
    }

    private static double Db(double amplitude) => 20 * Math.Log10(Math.Max(1e-6, amplitude));
    private static double Percentile(List<double> sorted, double fraction)
    {
        var position = fraction * (sorted.Count - 1);
        var lower = (int)position;
        return sorted[lower] + (sorted[Math.Min(lower + 1, sorted.Count - 1)] - sorted[lower]) * (position - lower);
    }

    private sealed class ArrayProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        public int Read(Span<float> buffer)
        {
            var count = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    private sealed class MonoAverageProvider(ISampleProvider source) : ISampleProvider
    {
        private float[] _buffer = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        public int Read(Span<float> buffer)
        {
            var channels = source.WaveFormat.Channels;
            var needed = buffer.Length * channels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            var read = source.Read(_buffer.AsSpan(0, needed));
            var frames = read / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                double sum = 0;
                for (var channel = 0; channel < channels; channel++) sum += _buffer[frame * channels + channel];
                buffer[frame] = (float)(sum / channels);
            }
            return frames;
        }
    }
}
