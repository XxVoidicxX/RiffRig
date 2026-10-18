namespace RiffRig.Dsp;

public enum AmpChannel { Clean, Drive }

/// <summary>Knob values, all 0..10 except where noted. Plain data so presets can copy it.</summary>
public sealed class AmpSettings
{
    public AmpChannel Channel = AmpChannel.Drive;
    public bool Boost = true;
    public bool Cab = true;
    public bool Enabled = true;
    public float Gate = 5;      // 0 = off
    public float Gain = 6;
    public float Bass = 5;
    public float Mid = 6;
    public float Treble = 6;
    public float Presence = 5;
    public float Volume = 5;

    public AmpSettings Clone() => (AmpSettings)MemberwiseClone();
}

public sealed record AmpPreset(string Name, AmpSettings Settings, int TuningIndex)
{
    public override string ToString() => Name;

    public static readonly AmpPreset[] All =
    {
        new("Clean", new AmpSettings { Channel = AmpChannel.Clean, Boost = false, Gate = 0, Gain = 3, Bass = 5, Mid = 5, Treble = 6, Presence = 5, Volume = 5 }, -1),
        new("Crunch", new AmpSettings { Channel = AmpChannel.Drive, Boost = false, Gate = 3, Gain = 3.5f, Bass = 5, Mid = 6, Treble = 6, Presence = 5, Volume = 5 }, -1),
        new("115 Metal", new AmpSettings { Channel = AmpChannel.Drive, Boost = true, Gate = 5, Gain = 6.5f, Bass = 4.5f, Mid = 6.5f, Treble = 6, Presence = 6, Volume = 5 }, 2),
        new("Djent Tight", new AmpSettings { Channel = AmpChannel.Drive, Boost = true, Gate = 7, Gain = 5.5f, Bass = 4, Mid = 7, Treble = 6.5f, Presence = 7, Volume = 5 }, 4),
        new("Dry (no amp)", new AmpSettings { Enabled = false, Gate = 0 }, -1),
    };
}

/// <summary>
/// Small guitar amp: gate, optional boost, one or two clipping stages at 4x oversampling,
/// tone stack, and a 4x12-style cab filter. Mono. Settings are applied at block start.
/// </summary>
public sealed class Amp
{
    const int Os = 4;
    double _sr = 48000;
    AmpSettings _s = new(), _pending;
    readonly object _lock = new();

    // Gate
    float _gateEnv, _gateGain = 1, _gateThresh, _gateHold;
    int _holdLeft;

    // Pre stage (boost and tightening), clip stages, tone, cab.
    readonly Biquad _preHp = new(), _boostMid = new();
    readonly Biquad _up1 = new(), _up2 = new(), _down1 = new(), _down2 = new();
    readonly Biquad _stageHp = new(), _stageLp = new();
    readonly Biquad _bass = new(), _mid = new(), _treble = new(), _presence = new();
    readonly Biquad _cabHp = new(), _cabRes = new(), _cabDip = new(), _cabBite = new(), _cabLp1 = new(), _cabLp2 = new();
    readonly Biquad _dcBlock = new();
    float _pre, _post, _vol, _prev;

    public void Prepare(int sampleRate)
    {
        _sr = sampleRate;
        Apply(_s);
        Reset();
    }

    public void Reset()
    {
        foreach (var b in new[] { _preHp, _boostMid, _up1, _up2, _down1, _down2, _stageHp, _stageLp,
                                  _bass, _mid, _treble, _presence, _cabHp, _cabRes, _cabDip, _cabBite, _cabLp1, _cabLp2, _dcBlock })
            b.Reset();
        _gateEnv = 0; _gateGain = 1; _holdLeft = 0; _prev = 0;
    }

    /// <summary>Thread safe. Takes effect on the next processed block.</summary>
    public void Set(AmpSettings s)
    {
        lock (_lock) _pending = s.Clone();
    }

    void Apply(AmpSettings s)
    {
        _s = s;
        double sr = _sr, osr = _sr * Os;

        _gateThresh = s.Gate <= 0 ? 0 : (float)DbToLin(-80 + s.Gate * 4.5); // -75 dB .. -35 dB
        _gateHold = 0.05f;

        bool drive = s.Channel == AmpChannel.Drive;
        _preHp.HighPass(sr, s.Boost ? 140 : (drive ? 90 : 40));
        _boostMid.Peak(sr, 800, 0.9, s.Boost ? 5 : 0);

        // Anti-imaging and anti-aliasing filters around the oversampled clipper.
        double nyq = Math.Min(18000, sr * 0.45);
        _up1.LowPass(osr, nyq, 0.54); _up2.LowPass(osr, nyq, 1.31);
        _down1.LowPass(osr, nyq, 0.54); _down2.LowPass(osr, nyq, 1.31);
        _stageHp.HighPass(osr, drive ? 160 : 20);
        _stageLp.LowPass(osr, drive ? 7000 : 12000);

        double gainDb = drive ? 14 + s.Gain * 4.6 : s.Gain * 2.4;   // drive 14..60 dB, clean 0..24 dB
        if (s.Boost) gainDb += 6;
        _pre = (float)Math.Pow(10, gainDb / 20);
        _post = drive ? 0.35f : 0.8f;

        _bass.LowShelf(sr, 120, (s.Bass - 5) * 2.4);
        _mid.Peak(sr, 750, 0.8, (s.Mid - 5) * 2.4);
        _treble.HighShelf(sr, 3000, (s.Treble - 5) * 2.4);
        _presence.Peak(sr, 5500, 0.9, (s.Presence - 5) * 1.8);

        _cabHp.HighPass(sr, 85, 0.8);
        _cabRes.Peak(sr, 115, 1.4, 3);
        _cabDip.Peak(sr, 450, 1.0, -4);
        _cabBite.Peak(sr, 2600, 1.2, 3);
        _cabLp1.LowPass(sr, 5200, 0.6);
        _cabLp2.LowPass(sr, 6500, 0.9);
        _dcBlock.HighPass(sr, 20);

        _vol = (float)Math.Pow(10, (s.Volume - 5) * 2.4 / 20) * 0.7f;
    }

    static double DbToLin(double db) => Math.Pow(10, db / 20);

    public void Process(Span<float> buf)
    {
        AmpSettings p = null;
        lock (_lock) { if (_pending != null) { p = _pending; _pending = null; } }
        if (p != null) Apply(p);
        var s = _s;
        if (!s.Enabled) return;

        float atk = 1 - MathF.Exp(-1f / (0.0008f * (float)_sr));
        float rel = 1 - MathF.Exp(-1f / (0.08f * (float)_sr));
        float envRel = 1 - MathF.Exp(-1f / (0.03f * (float)_sr));
        int holdSamples = (int)(_gateHold * _sr);
        bool drive = s.Channel == AmpChannel.Drive;

        for (int i = 0; i < buf.Length; i++)
        {
            float x = buf[i];

            // Noise gate keyed on the dry input, before any gain.
            if (_gateThresh > 0)
            {
                float a = MathF.Abs(x);
                _gateEnv = a > _gateEnv ? a : _gateEnv + (a - _gateEnv) * envRel;
                bool open = _gateEnv > _gateThresh;
                if (open) _holdLeft = holdSamples; else if (_holdLeft > 0) { _holdLeft--; open = true; }
                float target = open ? 1 : 0;
                _gateGain += (target - _gateGain) * (open ? atk : rel);
                x *= _gateGain;
            }

            x = _preHp.Process(x);
            if (s.Boost) x = _boostMid.Process(x);
            x *= _pre;

            // 4x oversampled clipper: linear interpolation up, filter, clip, filter, keep every 4th.
            float y = 0;
            for (int k = 1; k <= Os; k++)
            {
                float u = _prev + (x - _prev) * (k / (float)Os);
                u = _up2.Process(_up1.Process(u));
                float c = Clip(u + 0.08f) - Clip(0.08f);          // stage 1, slightly asymmetric
                if (drive)
                {
                    c = _stageLp.Process(_stageHp.Process(c));
                    c = Clip(c * 4.5f);                             // stage 2
                }
                y = _down2.Process(_down1.Process(c));
            }
            _prev = x;

            y = _dcBlock.Process(y) * _post;
            y = _presence.Process(_treble.Process(_mid.Process(_bass.Process(y))));
            if (s.Cab)
                y = _cabLp2.Process(_cabLp1.Process(_cabBite.Process(_cabDip.Process(_cabRes.Process(_cabHp.Process(y))))));
            buf[i] = y * _vol;
        }
    }

    static float Clip(float x) => MathF.Tanh(x);
}
