namespace RiffRig.Dsp;

/// <summary>RBJ cookbook biquad, transposed direct form II.</summary>
public sealed class Biquad
{
    float _b0 = 1, _b1, _b2, _a1, _a2, _z1, _z2;

    public void Reset() { _z1 = 0; _z2 = 0; }

    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    public Biquad LowPass(double sr, double f, double q = 0.7071)
    {
        Common(sr, f, q, out double w, out double cos, out double alpha);
        double b1 = 1 - cos;
        return Set(b1 / 2, b1, b1 / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public Biquad HighPass(double sr, double f, double q = 0.7071)
    {
        Common(sr, f, q, out double w, out double cos, out double alpha);
        double b1 = 1 + cos;
        return Set(b1 / 2, -b1, b1 / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public Biquad Peak(double sr, double f, double q, double db)
    {
        Common(sr, f, q, out double w, out double cos, out double alpha);
        double a = Math.Pow(10, db / 40);
        return Set(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a);
    }

    public Biquad LowShelf(double sr, double f, double db)
    {
        double a = Math.Pow(10, db / 40);
        Common(sr, f, 0.7071, out double w, out double cos, out double alpha);
        double s = 2 * Math.Sqrt(a) * alpha;
        return Set(a * ((a + 1) - (a - 1) * cos + s), 2 * a * ((a - 1) - (a + 1) * cos), a * ((a + 1) - (a - 1) * cos - s),
                   (a + 1) + (a - 1) * cos + s, -2 * ((a - 1) + (a + 1) * cos), (a + 1) + (a - 1) * cos - s);
    }

    public Biquad HighShelf(double sr, double f, double db)
    {
        double a = Math.Pow(10, db / 40);
        Common(sr, f, 0.7071, out double w, out double cos, out double alpha);
        double s = 2 * Math.Sqrt(a) * alpha;
        return Set(a * ((a + 1) + (a - 1) * cos + s), -2 * a * ((a - 1) + (a + 1) * cos), a * ((a + 1) + (a - 1) * cos - s),
                   (a + 1) - (a - 1) * cos + s, 2 * ((a - 1) - (a + 1) * cos), (a + 1) - (a - 1) * cos - s);
    }

    static void Common(double sr, double f, double q, out double w, out double cos, out double alpha)
    {
        f = Math.Clamp(f, 10, sr * 0.49);
        w = 2 * Math.PI * f / sr;
        cos = Math.Cos(w);
        alpha = Math.Sin(w) / (2 * q);
    }

    Biquad Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = (float)(b0 / a0); _b1 = (float)(b1 / a0); _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0); _a2 = (float)(a2 / a0);
        return this;
    }
}
