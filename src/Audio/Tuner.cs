using RiffRig.Dsp;

namespace RiffRig.Audio;

public readonly record struct TunerReading(bool Found, double Hz, double LevelDb, DateTime At);

/// <summary>Runs the pitch detector on a background thread and smooths the result for display.</summary>
public sealed class Tuner : IDisposable
{
    readonly Scope _scope;
    readonly Func<int> _sampleRate;
    readonly PitchDetector _detector = new();
    readonly double[] _recent = new double[5];
    int _recentCount, _recentPos;
    double _smoothMidi = double.NaN;
    TunerReading _latest;
    readonly object _lock = new();
    CancellationTokenSource _cts;

    public Tuner(Scope scope, Func<int> sampleRate)
    {
        _scope = scope;
        _sampleRate = sampleRate;
    }

    public TunerReading Latest { get { lock (_lock) return _latest; } }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var thread = new Thread(() => Loop(token)) { IsBackground = true, Name = "Tuner", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    void Loop(CancellationToken token)
    {
        long lastWritten = -1;
        float[] window = Array.Empty<float>();
        while (!token.IsCancellationRequested)
        {
            Thread.Sleep(30);
            long written = _scope.Written;
            if (written == lastWritten) continue;
            lastWritten = written;

            int sr = _sampleRate();
            int n = PitchDetector.WindowFor(sr);
            if (written < n) continue;
            if (window.Length != n) window = new float[n];
            _scope.CopyLatest(window);
            Push(_detector.Detect(window, sr));
        }
    }

    /// <summary>Median of the last few detections, then light smoothing in the pitch domain.</summary>
    internal void Push(PitchResult r)
    {
        var now = DateTime.UtcNow;
        if (!r.Found || r.Clarity < 0.75)
        {
            _recentCount = 0;
            lock (_lock) _latest = _latest with { LevelDb = r.LevelDb, Found = _latest.Found && (now - _latest.At).TotalMilliseconds < 350 };
            return;
        }

        double midi = Notes.Midi(r.Hz);
        _recent[_recentPos] = midi;
        _recentPos = (_recentPos + 1) % _recent.Length;
        _recentCount = Math.Min(_recentCount + 1, _recent.Length);
        var sorted = _recent.Take(_recentCount).Order().ToArray();
        double median = sorted[sorted.Length / 2];

        if (double.IsNaN(_smoothMidi) || Math.Abs(median - _smoothMidi) > 0.5) _smoothMidi = median;
        else _smoothMidi += (median - _smoothMidi) * 0.35;

        lock (_lock) _latest = new TunerReading(true, 440 * Math.Pow(2, (_smoothMidi - 69) / 12), r.LevelDb, now);
    }

    public void Dispose() => Stop();
}
