using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RiffRig.Dsp;

namespace RiffRig.Audio;

public sealed record DeviceInfo(string Id, string Name)
{
    static readonly System.Text.RegularExpressions.Regex Endpoint = new(@"^(Microphone|Speakers|Headphones|Headset|Line|Line In|Mic in at .*?) \((.+)\)$");

    /// <summary>"Microphone (USB Audio Device)" reads better as "USB Audio Device (Microphone)" in a narrow combo box.</summary>
    public override string ToString()
    {
        var m = Endpoint.Match(Name);
        return m.Success ? $"{m.Groups[2].Value} ({m.Groups[1].Value})" : Name;
    }
}

/// <summary>Ring buffer the tuner and meters read from. One writer (audio), one reader.</summary>
public sealed class Scope
{
    public const int Size = 8192;
    readonly float[] _buf = new float[Size];
    int _w;
    long _written;

    public long Written => Volatile.Read(ref _written);

    public void Write(ReadOnlySpan<float> s)
    {
        int w = _w;
        foreach (float x in s) { _buf[w] = x; w = (w + 1) & (Size - 1); }
        Volatile.Write(ref _w, w);
        Interlocked.Add(ref _written, s.Length);
    }

    public void CopyLatest(Span<float> dst)
    {
        int w = Volatile.Read(ref _w), n = dst.Length;
        for (int i = 0; i < n; i++) dst[i] = _buf[(w - n + i) & (Size - 1)];
    }

    public void Clear() { Array.Clear(_buf); Interlocked.Exchange(ref _written, 0); }
}

/// <summary>Feeds processed mono audio into a WASAPI render device, matching its mix format.</summary>
sealed class OutputTap : IWaveProvider
{
    const int RingSize = 1 << 16, Mask = RingSize - 1;
    readonly float[] _ring = new float[RingSize];
    readonly object _lock = new();
    readonly int _ch, _target;
    readonly double _step;
    long _w;
    double _r;
    bool _primed;

    public OutputTap(WaveFormat mix, int inRate, int latencyMs)
    {
        WaveFormat = mix;
        _ch = mix.Channels;
        _step = (double)inRate / mix.SampleRate;
        _target = Math.Max(64, (int)(inRate * latencyMs * 1.5 / 1000));
    }

    public WaveFormat WaveFormat { get; }
    public int Underruns { get; private set; }

    public void Write(ReadOnlySpan<float> s, float gain)
    {
        lock (_lock)
        {
            foreach (float x in s) { _ring[_w & Mask] = x * gain; _w++; }
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var dst = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
        int frames = dst.Length / _ch;
        lock (_lock)
        {
            double avail = _w - _r;
            if (!_primed)
            {
                if (avail < _target) { dst.Clear(); return count; }
                _primed = true;
            }
            // Input and output clocks drift apart. If we fall far behind, jump forward to keep latency low.
            if (avail > _target * 3 + 1024) _r = _w - _target;

            int f = 0;
            for (; f < frames; f++)
            {
                if (_r + 1 >= _w) { _primed = false; Underruns++; break; }
                long i = (long)_r;
                float frac = (float)(_r - i);
                float a = _ring[i & Mask];
                float v = a + (_ring[(i + 1) & Mask] - a) * frac;
                _r += _step;
                int o = f * _ch;
                for (int c = 0; c < _ch; c++) dst[o + c] = v;
            }
            if (f < frames) dst[(f * _ch)..].Clear();
        }
        return count;
    }
}

public sealed class AudioEngine : IDisposable
{
    static readonly Guid FloatSubType = new("00000003-0000-0010-8000-00aa00389b71");

    readonly MMDeviceEnumerator _enum = new();
    WasapiCapture _capture;
    WasapiOut _out, _send;
    OutputTap _outTap, _sendTap;
    WaveFormat _inFmt;
    bool _inFloat;
    float[] _mono = new float[16384];

    float _inGain = 1, _outGain = 1, _sendGain = 1;
    int _muted;
    float _muteMix = 1, _limEnv, _limGain = 1;
    float _inPeak, _outPeak;

    public Amp Amp { get; } = new();
    /// <summary>Input after input gain, before the amp. The tuner reads this.</summary>
    public Scope InScope { get; } = new();
    public bool IsRunning { get; private set; }
    public int SampleRate { get; private set; } = 48000;
    public string InputFormat { get; private set; } = "";
    public int Underruns => (_outTap?.Underruns ?? 0) + (_sendTap?.Underruns ?? 0);

    public event Action<string> Faulted;

    public float InputGainDb { set => Volatile.Write(ref _inGain, DbToLin(value)); }
    public float OutputGainDb { set => Volatile.Write(ref _outGain, DbToLin(value)); }
    public float SendGainDb { set => Volatile.Write(ref _sendGain, DbToLin(value)); }

    public bool Muted
    {
        get => Volatile.Read(ref _muted) == 1;
        set => Volatile.Write(ref _muted, value ? 1 : 0);
    }

    /// <summary>Peak levels since the last call, in linear units. Resets them.</summary>
    public (float input, float output) TakePeaks() =>
        (Interlocked.Exchange(ref _inPeak, 0), Interlocked.Exchange(ref _outPeak, 0));

    public static float DbToLin(float db) => MathF.Pow(10, db / 20);

    public List<DeviceInfo> GetDevices(DataFlow flow)
    {
        var list = new List<DeviceInfo>();
        foreach (var d in _enum.EnumerateAudioEndPoints(flow, DeviceState.Active))
            list.Add(new DeviceInfo(d.ID, d.FriendlyName));
        return list.OrderBy(d => d.Name).ToList();
    }

    public string DefaultId(DataFlow flow)
    {
        try { return _enum.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID; }
        catch { return null; }
    }

    public void Start(string inputId, string outputId, string sendId, int latencyMs)
    {
        Stop();
        try
        {
            _capture = new WasapiCapture(_enum.GetDevice(inputId), true, latencyMs);
            _inFmt = _capture.WaveFormat;
            _inFloat = _inFmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (_inFmt is WaveFormatExtensible ext && ext.SubFormat == FloatSubType && _inFmt.BitsPerSample == 32);
            SampleRate = _inFmt.SampleRate;
            InputFormat = $"{_inFmt.SampleRate / 1000.0:0.#} kHz, {_inFmt.Channels} ch";

            Amp.Prepare(SampleRate);
            _limEnv = 0; _limGain = 1;
            InScope.Clear();

            (_out, _outTap) = OpenOutput(_enum.GetDevice(outputId), latencyMs);
            if (!string.IsNullOrEmpty(sendId) && sendId != outputId)
                (_send, _sendTap) = OpenOutput(_enum.GetDevice(sendId), latencyMs);

            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) => { if (e.Exception != null) Faulted?.Invoke("Input: " + e.Exception.Message); };
            _out.PlaybackStopped += (_, e) => { if (e.Exception != null) Faulted?.Invoke("Output: " + e.Exception.Message); };
            if (_send != null)
                _send.PlaybackStopped += (_, e) => { if (e.Exception != null) Faulted?.Invoke("Send: " + e.Exception.Message); };

            _out.Play();
            _send?.Play();
            _capture.StartRecording();
            IsRunning = true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    (WasapiOut, OutputTap) OpenOutput(MMDevice dev, int latencyMs)
    {
        var mix = dev.AudioClient.MixFormat;
        if (mix.BitsPerSample != 32)
            mix = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
        var tap = new OutputTap(mix, SampleRate, latencyMs);
        var wo = new WasapiOut(dev, AudioClientShareMode.Shared, true, latencyMs);
        wo.Init(tap);
        return (wo, tap);
    }

    public void Stop()
    {
        IsRunning = false;
        try { _capture?.StopRecording(); } catch { }
        try { _out?.Stop(); } catch { }
        try { _send?.Stop(); } catch { }
        _capture?.Dispose(); _out?.Dispose(); _send?.Dispose();
        _capture = null; _out = null; _send = null; _outTap = null; _sendTap = null;
    }

    void OnData(object sender, WaveInEventArgs e)
    {
        try { Process(e.Buffer, e.BytesRecorded); }
        catch (Exception ex) { Faulted?.Invoke("Processing: " + ex.Message); }
    }

    void Process(byte[] data, int bytes)
    {
        int frames = bytes / _inFmt.BlockAlign;
        if (frames == 0) return;
        if (_mono.Length < frames) _mono = new float[frames * 2];
        var buf = _mono.AsSpan(0, frames);
        ToMono(data, frames, buf);

        float ig = Volatile.Read(ref _inGain), peak = 0;
        for (int i = 0; i < frames; i++)
        {
            float v = buf[i] * ig;
            buf[i] = v;
            float a = MathF.Abs(v);
            if (a > peak) peak = a;
        }
        InScope.Write(buf);
        MaxInto(ref _inPeak, peak);

        Amp.Process(buf);
        Finish(buf);

        _outTap?.Write(buf, 1f);
        _sendTap?.Write(buf, Volatile.Read(ref _sendGain));
    }

    /// <summary>Output gain, mute ramp and a safety limiter so nothing can blast your ears.</summary>
    void Finish(Span<float> buf)
    {
        float og = Volatile.Read(ref _outGain), muteTarget = Muted ? 0 : 1, peak = 0;
        float step = 1f / (0.02f * SampleRate);
        float relC = 1 - 1f / (0.12f * SampleRate);
        for (int i = 0; i < buf.Length; i++)
        {
            _muteMix = _muteMix < muteTarget ? Math.Min(muteTarget, _muteMix + step) : Math.Max(muteTarget, _muteMix - step);
            float x = buf[i] * og * _muteMix;
            if (float.IsNaN(x) || float.IsInfinity(x)) { x = 0; Amp.Reset(); }
            float a = MathF.Abs(x);
            _limEnv = a > _limEnv ? a : _limEnv * relC;
            float g = _limEnv > 0.9f ? 0.9f / _limEnv : 1f;
            _limGain = g < _limGain ? g : _limGain + (g - _limGain) * 0.002f;
            x = Math.Clamp(x * _limGain, -1f, 1f);
            buf[i] = x;
            if (MathF.Abs(x) > peak) peak = MathF.Abs(x);
        }
        MaxInto(ref _outPeak, peak);
    }

    static void MaxInto(ref float field, float v)
    {
        float cur;
        do { cur = Volatile.Read(ref field); if (v <= cur) return; }
        while (Interlocked.CompareExchange(ref field, v, cur) != cur);
    }

    void ToMono(byte[] data, int frames, Span<float> buf)
    {
        int ch = _inFmt.Channels;
        float inv = 1f / ch;
        if (_inFloat)
        {
            var src = MemoryMarshal.Cast<byte, float>(data.AsSpan(0, frames * _inFmt.BlockAlign));
            for (int f = 0; f < frames; f++)
            {
                float s = 0; int o = f * ch;
                for (int c = 0; c < ch; c++) s += src[o + c];
                buf[f] = s * inv;
            }
        }
        else if (_inFmt.BitsPerSample == 16)
        {
            var src = MemoryMarshal.Cast<byte, short>(data.AsSpan(0, frames * _inFmt.BlockAlign));
            for (int f = 0; f < frames; f++)
            {
                float s = 0; int o = f * ch;
                for (int c = 0; c < ch; c++) s += src[o + c] / 32768f;
                buf[f] = s * inv;
            }
        }
        else if (_inFmt.BitsPerSample == 24)
        {
            for (int f = 0; f < frames; f++)
            {
                float s = 0;
                for (int c = 0; c < ch; c++)
                {
                    int o = (f * ch + c) * 3;
                    int v = data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16);
                    s += v / 8388608f;
                }
                buf[f] = s * inv;
            }
        }
        else
        {
            var src = MemoryMarshal.Cast<byte, int>(data.AsSpan(0, frames * _inFmt.BlockAlign));
            for (int f = 0; f < frames; f++)
            {
                float s = 0; int o = f * ch;
                for (int c = 0; c < ch; c++) s += src[o + c] / 2147483648f;
                buf[f] = s * inv;
            }
        }
    }

    /// <summary>Offline path for the self test: runs a block through the same amp and limiter.</summary>
    internal void ProcessOffline(Span<float> buf)
    {
        InScope.Write(buf);
        Amp.Process(buf);
        Finish(buf);
    }

    internal void PrepareOffline(int sampleRate)
    {
        SampleRate = sampleRate;
        Amp.Prepare(sampleRate);
    }

    public void Dispose()
    {
        Stop();
        _enum.Dispose();
    }
}
