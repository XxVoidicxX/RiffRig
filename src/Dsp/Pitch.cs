namespace RiffRig.Dsp;

public readonly record struct PitchResult(bool Found, double Hz, double Clarity, double LevelDb);

/// <summary>
/// YIN pitch detector tuned for guitar. Covers about 50 Hz (drop tunings) up to 1.2 kHz.
/// Not real-time safe (allocates once per window size), so run it off the audio thread.
/// </summary>
public sealed class PitchDetector
{
    const double MinHz = 50, MaxHz = 1200, Threshold = 0.12;
    const int Settle = 128; // samples skipped while the pre-filter settles
    float[] _x = Array.Empty<float>(), _d = Array.Empty<float>();
    readonly Biquad _lp1 = new(), _lp2 = new();
    int _lpRate;

    /// <summary>Window length: one period of the lowest note plus about 50 ms to average over.</summary>
    public static int WindowFor(int sampleRate) => (int)(sampleRate / MinHz) + sampleRate / 20 + Settle + 4;

    public PitchResult Detect(ReadOnlySpan<float> input, int sampleRate)
    {
        int tauMax = (int)(sampleRate / MinHz);
        int tauMin = Math.Max(2, (int)(sampleRate / MaxHz));
        int n = input.Length - Settle;
        int w = n - tauMax - 2;
        if (w < tauMax) return default;
        if (_x.Length != n) _x = new float[n];
        if (_d.Length != tauMax + 2) _d = new float[tauMax + 2];

        // Low-pass well above the highest fundamental to cut hiss and pick noise. A delay does not change the period.
        if (_lpRate != sampleRate)
        {
            _lp1.LowPass(sampleRate, MaxHz * 1.5, 0.54);
            _lp2.LowPass(sampleRate, MaxHz * 1.5, 1.31);
            _lpRate = sampleRate;
        }
        _lp1.Reset(); _lp2.Reset();
        for (int i = 0; i < input.Length; i++)
        {
            float v = _lp2.Process(_lp1.Process(input[i]));
            if (i >= Settle) _x[i - Settle] = v;
        }

        // Remove DC and measure level.
        double mean = 0;
        for (int i = 0; i < n; i++) mean += _x[i];
        mean /= n;
        double sq = 0;
        for (int i = 0; i < n; i++) { float v = (float)(_x[i] - mean); _x[i] = v; sq += v * v; }
        double rms = Math.Sqrt(sq / n);
        double levelDb = 20 * Math.Log10(rms + 1e-12);
        if (levelDb < -60) return new PitchResult(false, 0, 0, levelDb);

        // Difference function, then cumulative mean normalized difference.
        var x = _x.AsSpan();
        var a = x[..w];
        _d[0] = 1;
        double running = 0;
        for (int tau = 1; tau <= tauMax + 1; tau++)
        {
            var b = x.Slice(tau, w);
            double sum = 0;
            for (int j = 0; j < w; j++) { float diff = a[j] - b[j]; sum += diff * diff; }
            running += sum;
            _d[tau] = running > 0 ? (float)(sum * tau / running) : 1;
        }

        int best = -1;
        for (int tau = tauMin; tau <= tauMax; tau++)
        {
            if (_d[tau] < Threshold)
            {
                while (tau + 1 <= tauMax && _d[tau + 1] < _d[tau]) tau++;
                best = tau;
                break;
            }
        }
        if (best < 0)
        {
            // No dip under the threshold: accept the global minimum only if it is still clear.
            float min = float.MaxValue;
            for (int tau = tauMin; tau <= tauMax; tau++)
                if (_d[tau] < min) { min = _d[tau]; best = tau; }
            if (min > 0.3) return new PitchResult(false, 0, 1 - min, levelDb);
        }

        // Parabolic interpolation for sub-sample accuracy.
        double t = best;
        if (best > 1 && best < tauMax + 1)
        {
            double s0 = _d[best - 1], s1 = _d[best], s2 = _d[best + 1];
            double den = s0 - 2 * s1 + s2;
            if (Math.Abs(den) > 1e-12) t = best + 0.5 * (s0 - s2) / den;
        }
        return new PitchResult(true, sampleRate / t, 1 - _d[best], levelDb);
    }
}

public sealed record GuitarTuning(string Name, double[] Hz)
{
    public override string ToString() => Name;
}

public static class Notes
{
    static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static double Midi(double hz, double a4 = 440) => 69 + 12 * Math.Log2(hz / a4);
    public static double Hz(int midi, double a4 = 440) => a4 * Math.Pow(2, (midi - 69) / 12.0);
    public static double Cents(double hz, double targetHz) => 1200 * Math.Log2(hz / targetHz);

    public static string Name(int midi) => Names[((midi % 12) + 12) % 12];
    public static string NameOct(int midi) => Name(midi) + (midi / 12 - 1);

    static GuitarTuning T(string name, params int[] midi) => new(name, midi.Select(m => Hz(m)).ToArray());

    /// <summary>Six-string tunings, low string first. Midi: E2 = 40.</summary>
    public static readonly GuitarTuning[] Tunings =
    {
        T("E Standard", 40, 45, 50, 55, 59, 64),
        T("Eb Standard", 39, 44, 49, 54, 58, 63),
        T("D Standard (115)", 38, 43, 48, 53, 57, 62),
        T("Drop D", 38, 45, 50, 55, 59, 64),
        T("Drop C", 36, 43, 48, 53, 57, 62),
        T("C Standard", 36, 41, 46, 51, 55, 60),
        new("Chromatic", Array.Empty<double>()),
    };
}
