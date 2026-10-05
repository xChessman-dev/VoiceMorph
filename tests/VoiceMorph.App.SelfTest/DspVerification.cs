using NAudio.Wave;
using VoiceMorph.App.Models;
using VoiceMorph.App.Services.Dsp;

public static class DspVerification
{
    public static void Run(Action<bool, string> check)
    {
        check(VoiceParameter.All.Select(p => p.Key).Distinct().Count() == VoiceParameter.All.Count,
            "Параметры DSP имеют уникальные ключи и единые диапазоны");
        var hostile = VoiceSettings.Neutral with
        {
            PitchSemitones = float.NaN, Tone = float.PositiveInfinity, BassDb = 500,
            Compression = -100, Strength = float.NegativeInfinity, LimiterCeilingDb = 50,
        };
        var safe = hostile.Sanitize();
        check(VoiceParameter.All.All(p => float.IsFinite(p.Get(safe)) && p.Get(safe) >= p.Min && p.Get(safe) <= p.Max),
            "Повреждённые настройки приводятся к конечным безопасным значениям");
        var original = VoiceSettings.FromPreset(VoicePreset.BuiltIns[0]) with { BassDb = 4 };
        var zero = original.AtStrength(0);
        check(zero.PitchSemitones == 0 && zero.FormantSemitones == 0 && zero.BassDb == 0 && zero.Compression == 0 &&
              zero.LimiterCeilingDb == original.LimiterCeilingDb && zero.GateThresholdDb == original.GateThresholdDb,
            "Сила 0% снимает преобразование и сохраняет параметры защиты");

        var envelope = new FormantEnvelopeEffect();
        envelope.Configure(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1));
        var impulse = new float[4096];
        impulse[1200] = .2f;
        for (var offset = 0; offset < impulse.Length; offset += 256)
            envelope.Process(impulse.AsSpan(offset, 256));
        var impulsePeak = Enumerable.Range(0, impulse.Length).MaxBy(i => Math.Abs(impulse[i]));
        check(impulsePeak - 1200 == envelope.LatencySamples && Math.Abs(impulse[impulsePeak] - .2f) < .001f,
            "Задержка формантного процессора подтверждена импульсом, уровень сохраняется");

        var sine = Signal(48_000, i => .15f * MathF.Sin(2 * MathF.PI * 200 * i / 48_000));
        var neutral = Render(sine, VoiceSettings.Neutral);
        check(neutral.Samples.All(float.IsFinite) && Rms(neutral.Samples, 12_000, 24_000) is > .08 and < .14,
            $"Нейтральный DSP сохраняет уровень тестового голоса без численных артефактов: RMS {Rms(neutral.Samples, 12_000, 24_000):F4}");
        var shifted = Render(sine, VoiceSettings.Neutral with { PitchSemitones = 3, FormantPreservation = 0 });
        var frequency = EstimateFrequency(shifted.Samples, 48_000);
        check(Math.Abs(frequency - 237.84f) < 6,
            $"Изменение высоты реально перемещает основной тон: {frequency:F1} Hz (ожидается ≈238)");

        var vowel = Signal(48_000, i =>
        {
            var value = 0f;
            for (var harmonic = 1; harmonic <= 45; harmonic++)
            {
                var frequencyHz = harmonic * 140f;
                var envelope = MathF.Exp(-MathF.Pow((frequencyHz - 700) / 250, 2)) +
                               .6f * MathF.Exp(-MathF.Pow((frequencyHz - 1_700) / 350, 2));
                value += envelope * MathF.Sin(2 * MathF.PI * frequencyHz * i / 48_000) * .015f;
            }
            return value;
        });
        var plainVowel = Render(vowel, VoiceSettings.Neutral);
        var formantVowel = Render(vowel, VoiceSettings.Neutral with { FormantSemitones = 3 });
        var plainCentroid = SpectralCentroid(plainVowel.Samples);
        var movedCentroid = SpectralCentroid(formantVowel.Samples);
        check(movedCentroid > plainCentroid + 25,
            $"Формантный регулятор перемещает спектральный центр: {plainCentroid:F0} → {movedCentroid:F0} Hz");
        var brightVowel = Render(vowel, VoiceSettings.Neutral with { PresenceDb = 5, AirDb = 4 });
        check(RmsDifference(plainVowel.Samples, brightVowel.Samples, 12_000, 24_000) > .002,
            "Дополнительные регуляторы тембра воздействуют на сигнал");

        var dangerous = Signal(24_000, i => i % 29 == 0 ? float.NaN : i % 37 == 0 ? float.PositiveInfinity : 8);
        var limited = Render(dangerous, original with { OutputGainDb = 6, LimiterCeilingDb = -3 });
        var ceiling = MathF.Pow(10, -3f / 20);
        check(limited.Samples.All(v => float.IsFinite(v) && Math.Abs(v) <= ceiling + .00001f),
            "NaN, бесконечность и перегруз не выходят из защитного лимитера");

        var bypassed = Render(sine, original, true);
        var aligned = Signal(bypassed.Samples.Length, i =>
            i >= bypassed.Latency && i - bypassed.Latency < sine.Length ? sine[i - bypassed.Latency] : 0);
        check(RmsDifference(bypassed.Samples, aligned, 12_000, 24_000) < .02,
            "Обход эффектов возвращает исходный голос с согласованной задержкой");
        var noStrength = Render(sine, original with { Strength = 0 });
        check(RmsDifference(noStrength.Samples, bypassed.Samples, 12_000, 24_000) < .002,
            "0% силы даёт тот же исходный голос, что обход эффектов");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var longSignal = Signal(48_000 * 3, i => sine[i % sine.Length]);
        var timed = Render(longSignal, original);
        clock.Stop();
        check(timed.Samples.All(float.IsFinite) && clock.Elapsed.TotalSeconds < 3,
            $"Трёхсекундный аудиофрагмент обработан за {clock.Elapsed.TotalMilliseconds:F0} ms");
    }

    private static (float[] Samples, int Latency) Render(float[] input, VoiceSettings settings, bool bypass = false)
    {
        var padded = new float[input.Length + 8192];
        input.CopyTo(padded, 0);
        var processor = new VoiceDspProcessor(new ArraySource(padded), settings);
        processor.SetBypass(bypass);
        var output = new float[padded.Length];
        var offset = 0;
        while (offset < output.Length)
        {
            var count = processor.Read(output.AsSpan(offset, Math.Min(480, output.Length - offset)));
            if (count == 0) break;
            offset += count;
        }
        return (output, processor.LatencySamples);
    }

    private static float[] Signal(int count, Func<int, float> sample) => Enumerable.Range(0, count).Select(sample).ToArray();

    private static double Rms(float[] signal, int start, int count) =>
        Math.Sqrt(signal.Skip(start).Take(count).Select(v => (double)v * v).Average());

    private static double RmsDifference(float[] a, float[] b, int start, int count) =>
        Math.Sqrt(Enumerable.Range(start, count).Select(i => Math.Pow(a[i] - b[i], 2)).Average());

    private static float EstimateFrequency(float[] samples, int rate)
    {
        var start = rate / 3;
        var crossings = new List<int>();
        for (var i = start + 1; i < start + rate / 3; i++)
            if (samples[i - 1] <= 0 && samples[i] > 0) crossings.Add(i);
        return crossings.Count < 2 ? 0 : rate * (crossings.Count - 1f) / (crossings[^1] - crossings[0]);
    }

    private static double SpectralCentroid(float[] samples)
    {
        const int size = 4096;
        var fft = new NAudio.Dsp.Complex[size];
        for (var i = 0; i < size; i++)
            fft[i].X = samples[12_000 + i] * (.5f - .5f * MathF.Cos(2 * MathF.PI * i / size));
        NAudio.Dsp.FastFourierTransform.FFT(true, 12, fft);
        double weighted = 0, sum = 0;
        for (var bin = 8; bin < 341; bin++)
        {
            var power = (double)fft[bin].X * fft[bin].X + (double)fft[bin].Y * fft[bin].Y;
            weighted += power * bin * 48_000d / size;
            sum += power;
        }
        return weighted / Math.Max(sum, 1e-20);
    }

    private sealed class ArraySource(float[] samples) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        public int Read(Span<float> buffer)
        {
            var count = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }
}
