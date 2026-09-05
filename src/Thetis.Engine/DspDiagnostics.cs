using System.Diagnostics;

namespace Thetis.Engine;

public sealed record DspCheck(string Name, double Measured, string Requirement, bool Passed);
public sealed record DspSelfTestResult(int SchemaVersion, DspAbiInfo Abi, bool Passed,
    long ElapsedMilliseconds, IReadOnlyList<DspCheck> Checks);

/// <summary>Offline diagnostic harness, not a radio-session or audio-device API.</summary>
public static class DspDiagnostics
{
    public static DspSelfTestResult Run(string nativeDirectory, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        DspAbiInfo abi = DspRuntime.Initialize(nativeDirectory);
        lock (DspRuntime.Gate)
        {
            OfflineRadioSession.RequireIdle();
            var checks = new List<DspCheck>();
            // Do not spend minutes producing machine-specific FFTW wisdom in a smoke test.
            NativeMethods.ThetisWdspSetPlanningTimeLimit(0);
            try
            {
                foreach (var (input, output) in new[] { (48000, 96000), (96000, 48000), (192000, 48000), (48000, 44100) })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double gain = MeasureResampler(input, output, 1500);
                    checks.Add(new($"Resampler {input}->{output} passband", gain, "complex RMS 0.2 ± 0.002", Math.Abs(gain - 0.2) < 0.002));
                }
                double stop = MeasureResampler(96000, 48000, 35000);
                checks.Add(new("Resampler anti-alias rejection", stop, "complex RMS < 0.0002", stop < 0.0002));
                double impulseError = ImpulseRepeatError();
                checks.Add(new("Impulse flush/replay", impulseError, "max difference < 1e-12", impulseError < 1e-12));
                cancellationToken.ThrowIfCancellationRequested();
                var spectrum = MeasureSpectrum(cancellationToken);
                checks.Add(new("Analyzer peak", spectrum.Peak, "pixel 544 ± 1 (1500 Hz at 48 kHz, FFT 2048, 1024 pixels)", Math.Abs(spectrum.Peak - 544) <= 1));
                checks.Add(new("Analyzer pixel-reference ABI", spectrum.Reference, "exactly 14.2", spectrum.Reference == 14.2));
                checks.Add(new("Analyzer amplitude scaling", spectrum.PeakDb, "-20 dB ± 0.2 relative to unit complex input, not dBm", Math.Abs(spectrum.PeakDb + 20) < 0.2));
                var receiver = MeasureReceiver(cancellationToken);
                checks.Add(new("RX USB passband", receiver.Pass, "RMS > 0.001 and < 1", receiver.Pass > 0.001 && receiver.Pass < 1));
                double rejection = 20 * Math.Log10(Math.Max(receiver.Stop, 1e-30) / Math.Max(receiver.Pass, 1e-30));
                checks.Add(new("RX USB stopband", rejection, "8000 Hz versus 1500 Hz < -50 dB", rejection < -50));
            }
            finally { NativeMethods.ThetisWdspSetPlanningTimeLimit(-1); }
            return new(1, abi, checks.All(c => c.Passed), timer.ElapsedMilliseconds, checks);
        }
    }

    private static void Tone(double[] iq, int offset, int rate, double frequency, double amplitude = 0.2)
    {
        for (int i = 0; i < iq.Length / 2; i++)
        {
            double phase = 2 * Math.PI * frequency * (offset + i) / rate;
            iq[2*i] = amplitude * Math.Cos(phase);
            iq[2*i+1] = amplitude * Math.Sin(phase);
        }
    }

    private static void CheckFinite(IEnumerable<double> samples)
    {
        if (samples.Any(x => !double.IsFinite(x))) throw new InvalidDataException("DSP produced NaN or infinity.");
    }

    internal static unsafe double MeasureResampler(int inputRate, int outputRate, double frequency)
    {
        const int size = 960;
        double[] input = new double[size * 2];
        int expected = size * outputRate / inputRate;
        double[] output = new double[(expected + 1) * 2];
        double energy = 0;
        int count = 0;
        fixed (double* pin = input, pout = output)
        {
            nint resampler = NativeMethods.create_resample(1, size, pin, pout, inputRate, outputRate, 0, 0, 1);
            if (resampler == 0) throw new OutOfMemoryException("Cannot create WDSP resampler.");
            try
            {
                for (int block = 0; block < 20; ++block)
                {
                    Tone(input, block * size, inputRate, frequency);
                    int samples = NativeMethods.xresample(resampler);
                    if (samples != expected) throw new InvalidDataException($"Expected {expected} resampled frames; got {samples}.");
                    CheckFinite(output);
                    if (block >= 4)
                    {
                        for (int i = 0; i < 2 * samples; ++i) energy += output[i] * output[i];
                        count += samples;
                    }
                }
            }
            finally { NativeMethods.destroy_resample(resampler); }
        }
        return Math.Sqrt(energy / count);
    }

    internal static unsafe double ImpulseRepeatError()
    {
        double[] input = new double[1920], output = new double[960];
        input[0] = 1;
        fixed (double* pin = input, pout = output)
        {
            nint resampler = NativeMethods.create_resample(1, 960, pin, pout, 96000, 48000, 0, 0, 1);
            if (resampler == 0) throw new OutOfMemoryException();
            try
            {
                if (NativeMethods.xresample(resampler) != 480) throw new InvalidDataException();
                CheckFinite(output);
                double[] first = (double[])output.Clone();
                if (first.Sum(x => x*x) < 1e-8) throw new InvalidDataException("Impulse response is empty.");
                NativeMethods.flush_resample(resampler);
                if (NativeMethods.xresample(resampler) != 480) throw new InvalidDataException();
                CheckFinite(output);
                return first.Zip(output, (a, b) => Math.Abs(a-b)).Max();
            }
            finally { NativeMethods.destroy_resample(resampler); }
        }
    }

    internal static (int Peak, double Reference, double PeakDb) MeasureSpectrum(CancellationToken token)
    {
        const int display = 0, size = 2048, pixelCount = 1024;
        NativeMethods.XCreateAnalyzer(display, out int success, size, 1, 1, "");
        if (success != 0) throw new OutOfMemoryException($"Analyzer creation failed: {success}");
        try
        {
            NativeMethods.SetAnalyzer(display, 1, 1, 1, [0], size, size, 0, 14, 0,
                0, 0, 0, pixelCount, 1, 0, 0, 0, size);
            NativeMethods.SetPixelRef(display, 14.2);
            double[] input = new double[size * 2];
            Tone(input, 0, 48000, 1500, 0.1);
            // Unlike fexchange0, the legacy Spectrum0 entry point takes Q,I.
            for (int i = 0; i < size; ++i)
                (input[2*i], input[2*i+1]) = (input[2*i+1], input[2*i]);
            NativeMethods.Spectrum0(1, display, 0, 0, input);
            float[] pixels = new float[pixelCount];
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                token.ThrowIfCancellationRequested();
                NativeMethods.GetPixels(display, 0, pixels, out int ready, out double reference);
                if (ready != 0)
                {
                    if (pixels.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Analyzer returned nonfinite pixels.");
                    int peak = Array.IndexOf(pixels, pixels.Max());
                    return (peak, reference, pixels[peak]);
                }
                Thread.Sleep(5);
            }
            throw new TimeoutException("Analyzer did not deliver a frame within five seconds.");
        }
        finally { NativeMethods.DestroyAnalyzer(display); }
    }

    internal static (double Pass, double Stop) MeasureReceiver(CancellationToken token)
    {
        const int channel = 0, size = 1024;
        NativeMethods.OpenChannel(channel, size, size, 48000, 48000, 48000, 0, 1, 0, 0.005, 0, 0.005, 1);
        try
        {
            NativeMethods.SetRXAMode(channel, 1); // USB
            NativeMethods.SetRXABandpassFreqs(channel, 300, 3000);
            NativeMethods.SetRXAAGCMode(channel, 0);
            NativeMethods.SetRXAAGCFixed(channel, 0);
            double[] input = new double[size * 2], output = new double[size * 2];
            double[] levels = new double[2];
            for (int tone = 0; tone < 2; ++tone)
            {
                double energy = 0;
                for (int block = 0; block < 80; ++block)
                {
                    token.ThrowIfCancellationRequested();
                    Tone(input, block * size, 48000, tone == 0 ? 1500 : 8000, 0.1);
                    NativeMethods.fexchange0(channel, input, output, out int error);
                    if (error != 0) throw new InvalidDataException($"WDSP exchange error {error}.");
                    CheckFinite(output);
                    if (block >= 40) energy += output.Sum(x => x*x);
                }
                levels[tone] = Math.Sqrt(energy / (40 * size * 2));
            }
            return (levels[0], levels[1]);
        }
        finally { NativeMethods.CloseChannel(channel); }
    }
}
