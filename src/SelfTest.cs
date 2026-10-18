using System.Diagnostics;
using System.IO;
using System.Text;
using NAudio.CoreAudioApi;
using RiffRig.Audio;
using RiffRig.Dsp;

namespace RiffRig;

/// <summary>
/// Offline checks for the tuner and amp, plus an optional short run on the real devices.
/// Usage: RiffRig.exe --selftest out.txt [live]
/// </summary>
static class SelfTest
{
    public static int Run(string outPath, bool live)
    {
        var log = new StringBuilder();
        bool ok = true;
        void Log(string s) => log.AppendLine(s);

        try
        {
            ok &= TunerAccuracy(Log);
            ok &= TunerSmoothing(Log);
            ok &= AmpChecks(Log);
            if (live) ok &= LiveRun(Log);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION " + ex);
            ok = false;
        }
        Log(ok ? "RESULT PASS" : "RESULT FAIL");
        File.WriteAllText(outPath, log.ToString());
        return ok ? 0 : 1;
    }

    /// <summary>Plucked-string stand-in: weak fundamental, strong 2nd harmonic, stiffness, decay and hiss.</summary>
    static float[] Pluck(double hz, int sr, double seconds, double inharmonicity, int seed = 1)
    {
        var rnd = new Random(seed);
        int n = (int)(sr * seconds);
        var x = new float[n];
        double[] amps = { 0.45, 1.0, 0.7, 0.5, 0.35, 0.25, 0.18, 0.12, 0.08, 0.05 };
        for (int k = 1; k <= amps.Length; k++)
        {
            double f = hz * k * Math.Sqrt(1 + inharmonicity * k * k);
            if (f > sr * 0.45) break;
            double ph = rnd.NextDouble() * Math.PI * 2, decay = 1.2 + k * 0.6;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / sr;
                x[i] += (float)(amps[k - 1] * Math.Exp(-decay * t) * Math.Sin(2 * Math.PI * f * t + ph));
            }
        }
        float peak = x.Max(MathF.Abs);
        for (int i = 0; i < n; i++) x[i] = x[i] / peak * 0.3f + (float)((rnd.NextDouble() - 0.5) * 0.002);
        return x;
    }

    static bool TunerAccuracy(Action<string> log)
    {
        log("== Tuner accuracy (synthetic plucks, window taken 0.25 s and 1.2 s after the attack)");
        var det = new PitchDetector();
        bool ok = true;
        double worstPure = 0, worstStiff = 0;
        int checks = 0;
        foreach (int sr in new[] { 44100, 48000 })
        {
            int win = PitchDetector.WindowFor(sr);
            foreach (var t in Notes.Tunings.Where(t => t.Hz.Length > 0))
            {
                for (int s = 0; s < t.Hz.Length; s++)
                {
                    foreach (double offset in new[] { 0.0, 12.0, -37.0 })
                    {
                        double hz = t.Hz[s] * Math.Pow(2, offset / 1200);
                        foreach (double b in new[] { 0.0, 0.0001 })
                        {
                            var sig = Pluck(hz, sr, 1.6, b, s * 7 + 3);
                            foreach (double at in new[] { 0.25, 1.2 })
                            {
                                var r = det.Detect(sig.AsSpan((int)(at * sr), win), sr);
                                double err = r.Found ? Notes.Cents(r.Hz, hz) : double.NaN;
                                checks++;
                                if (b == 0) worstPure = Math.Max(worstPure, double.IsNaN(err) ? 999 : Math.Abs(err));
                                else worstStiff = Math.Max(worstStiff, double.IsNaN(err) ? 999 : Math.Abs(err));
                                double limit = b == 0 ? 0.5 : 2.5;
                                if (!r.Found || Math.Abs(err) > limit)
                                {
                                    ok = false;
                                    log($"  FAIL {sr} Hz {t.Name} string {6 - s} offset {offset:+0;-0;0}c B={b} t={at}s: " +
                                        (r.Found ? $"got {r.Hz:0.000} Hz, error {err:+0.00;-0.00} cents" : "no pitch"));
                                }
                            }
                        }
                    }
                }
            }
        }
        log($"  {checks} checks. Worst error: {worstPure:0.000} cents (ideal string), {worstStiff:0.000} cents (stiff string)");

        // Silence must report nothing.
        var quiet = new float[PitchDetector.WindowFor(48000)];
        var q = det.Detect(quiet, 48000);
        if (q.Found) { ok = false; log("  FAIL silence detected as a pitch"); }

        var sw = Stopwatch.StartNew();
        var probe = Pluck(82.41, 48000, 0.5, 0.0001);
        for (int i = 0; i < 50; i++) det.Detect(probe.AsSpan(0, PitchDetector.WindowFor(48000)), 48000);
        log($"  Detect cost: {sw.Elapsed.TotalMilliseconds / 50:0.00} ms per call at 48 kHz (runs about 30 times a second)");
        log(ok ? "  PASS" : "  FAIL");
        return ok;
    }

    static bool TunerSmoothing(Action<string> log)
    {
        log("== Tuner smoothing");
        var tuner = new Tuner(new Scope(), () => 48000);
        var rnd = new Random(5);
        double target = 146.83; // D3
        for (int i = 0; i < 30; i++)
            tuner.Push(new PitchResult(true, target * Math.Pow(2, (rnd.NextDouble() - 0.5) * 4 / 1200), 0.95, -20));
        var r = tuner.Latest;
        double err = Notes.Cents(r.Hz, target);
        tuner.Push(new PitchResult(true, target * 2, 0.95, -20));          // one octave glitch
        double afterGlitch = Notes.Cents(tuner.Latest.Hz, target);
        bool ok = r.Found && Math.Abs(err) < 1.5 && Math.Abs(afterGlitch) < 1.5;
        log($"  jittered input +/-2 cents settles at {err:+0.00;-0.00} cents, single octave glitch leaves it at {afterGlitch:+0.00;-0.00} cents");
        log(ok ? "  PASS" : "  FAIL");
        return ok;
    }

    static bool AmpChecks(Action<string> log)
    {
        log("== Amp presets (2 s pluck on the low string of the preset tuning, peak -10 dBFS)");
        bool ok = true;
        const int sr = 48000;
        foreach (var p in AmpPreset.All)
        {
            var engine = new AudioEngine();
            engine.PrepareOffline(sr);
            engine.Amp.Set(p.Settings);
            var tuning = Notes.Tunings[p.TuningIndex >= 0 ? p.TuningIndex : 0];
            var sig = Pluck(tuning.Hz[0], sr, 2, 0.0001);
            for (int i = 0; i < sig.Length; i++) sig[i] *= 1.05f; // about -10 dBFS

            var sw = Stopwatch.StartNew();
            double sq = 0; float peak = 0; bool finite = true;
            for (int off = 0; off < sig.Length; off += 480)
            {
                var block = sig.AsSpan(off, Math.Min(480, sig.Length - off));
                engine.ProcessOffline(block);
                foreach (float v in block)
                {
                    if (!float.IsFinite(v)) finite = false;
                    sq += v * v; peak = Math.Max(peak, Math.Abs(v));
                }
            }
            double cpu = sw.Elapsed.TotalSeconds / 2 * 100;
            double rmsDb = 10 * Math.Log10(sq / sig.Length + 1e-20), peakDb = 20 * Math.Log10(peak + 1e-9);

            // Gate: low hiss alone should be pushed well down when the gate is on.
            var hiss = new float[sr];
            var rnd = new Random(9);
            for (int i = 0; i < hiss.Length; i++) hiss[i] = (float)((rnd.NextDouble() - 0.5) * 0.0006);
            engine.Amp.Reset();
            for (int off = 0; off < hiss.Length; off += 480) engine.ProcessOffline(hiss.AsSpan(off, 480));
            double hissSq = 0;
            for (int i = sr / 2; i < sr; i++) hissSq += hiss[i] * hiss[i];
            double hissDb = 10 * Math.Log10(hissSq / (sr / 2) + 1e-20);

            bool good = finite && peak > 0.02 && peakDb <= 0.01 && cpu < 25 && (p.Settings.Gate <= 0 || hissDb < -70);
            ok &= good;
            log($"  {(good ? "ok  " : "FAIL")} {p.Name,-14} out peak {peakDb,6:0.0} dBFS, rms {rmsDb,6:0.0} dBFS, hiss after gate {hissDb,6:0.0} dBFS, CPU {cpu:0.0}% of one core");
        }
        log(ok ? "  PASS" : "  FAIL");
        return ok;
    }

    static bool LiveRun(Action<string> log)
    {
        log("== Live run (3 s on the real devices, nothing needs to be played)");
        using var engine = new AudioEngine();
        var inputs = engine.GetDevices(DataFlow.Capture);
        var inDev = inputs.FirstOrDefault(d => d.Name.Contains("USB Audio", StringComparison.OrdinalIgnoreCase)) ?? inputs.FirstOrDefault();
        string outId = engine.DefaultId(DataFlow.Render);
        if (inDev == null || outId == null) { log("  no devices, skipped"); return true; }
        log($"  input: {inDev.Name}");
        log($"  output: {engine.GetDevices(DataFlow.Render).FirstOrDefault(d => d.Id == outId)?.Name}");

        engine.OutputGainDb = -60; // keep it silent
        engine.Start(inDev.Id, outId, null, 10);
        var tuner = new Tuner(engine.InScope, () => engine.SampleRate);
        tuner.Start();
        float maxIn = 0;
        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(100);
            maxIn = Math.Max(maxIn, engine.TakePeaks().input);
        }
        var reading = tuner.Latest;
        tuner.Stop();
        long samples = engine.InScope.Written;
        int under = engine.Underruns;
        engine.Stop();

        double seconds = samples / (double)engine.SampleRate;
        bool ok = samples > engine.SampleRate * 2;
        log($"  format {engine.InputFormat}, captured {seconds:0.00} s of audio, input peak {20 * Math.Log10(maxIn + 1e-9):0.0} dBFS, dropouts {under}");
        log("  tuner: " + (reading.Found ? $"{reading.Hz:0.0} Hz" : "no note (expected if nothing was played)"));
        log(ok ? "  PASS" : "  FAIL (audio did not flow)");
        return ok;
    }
}
