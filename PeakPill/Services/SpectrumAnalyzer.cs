using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace PeakPill.Services;

/// <summary>
/// Real frequency bands of what is playing (loopback of the default output) for the bar chart in the closed island:
/// bar 1 = bass … bar 5 = treble. Runs only while the bars are shown.
/// </summary>
public sealed class SpectrumAnalyzer : IDisposable
{
    private const int N = 1024; // FFT size (~21 ms at 48 kHz)
    private static readonly (double Lo, double Hi)[] Bands = { (40, 140), (140, 400), (400, 1200), (1200, 3500), (3500, 10000) };

    private WasapiLoopbackCapture _cap;
    private readonly float[] _ring = new float[N];
    private int _pos;
    private readonly object _lock = new();
    private readonly double[] _max = { 1e-4, 1e-4, 1e-4, 1e-4, 1e-4 };
    private int _rate = 48000;

    public bool Running => _cap != null;

    public void Start()
    {
        if (_cap != null) return;
        try
        {
            _cap = new WasapiLoopbackCapture();
            _rate = _cap.WaveFormat.SampleRate;
            _cap.DataAvailable += OnData;
            _cap.RecordingStopped += (_, _) => { };
            _cap.StartRecording();
        }
        catch { _cap?.Dispose(); _cap = null; }
    }

    public void Stop()
    {
        var c = _cap;
        _cap = null;
        if (c == null) return;
        try { c.StopRecording(); } catch { }
        c.Dispose();
    }

    private void OnData(object sender, WaveInEventArgs e)
    {
        var fmt = ((WasapiLoopbackCapture)sender).WaveFormat;
        int ch = fmt.Channels;
        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat && fmt.Encoding != WaveFormatEncoding.Extensible) return;
        if (fmt.BitsPerSample != 32) return;
        int frames = e.BytesRecorded / (4 * ch);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, frames * ch * 4));
        lock (_lock)
        {
            for (int i = 0; i < frames; i++)
            {
                float s = 0;
                for (int c = 0; c < ch; c++) s += samples[i * ch + c];
                _ring[_pos] = s / ch;
                _pos = (_pos + 1) % N;
            }
        }
    }

    /// <summary>Five levels 0…1 (auto-scaled per band, so quiet songs move as much as loud ones).</summary>
    // reused every frame (the bars ask 25 times a second) – no garbage, the window is computed once
    private readonly Complex[] _buf = new Complex[N];
    private readonly double[] _res = new double[Bands.Length];
    private static readonly float[] Hann = Enumerable.Range(0, N).Select(i => (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (N - 1))))).ToArray();

    public double[] Levels()
    {
        var buf = _buf;
        lock (_lock)
        {
            for (int i = 0; i < N; i++)
            {
                buf[i].X = _ring[(_pos + i) % N] * Hann[i];
                buf[i].Y = 0;
            }
        }
        FastFourierTransform.FFT(true, (int)Math.Log2(N), buf);
        var res = _res;
        double hz = (double)_rate / N;
        for (int b = 0; b < Bands.Length; b++)
        {
            int lo = Math.Max(1, (int)(Bands[b].Lo / hz)), hi = Math.Min(N / 2 - 1, (int)(Bands[b].Hi / hz));
            double sum = 0;
            for (int k = lo; k <= hi; k++) sum += Math.Sqrt(buf[k].X * buf[k].X + buf[k].Y * buf[k].Y);
            double mag = sum / Math.Max(1, hi - lo + 1);
            // slowly decaying per-band maximum = automatic gain
            _max[b] = Math.Max(mag, _max[b] * 0.995);
            if (_max[b] < 1e-4) _max[b] = 1e-4;
            res[b] = mag < 1e-6 ? 0 : Math.Clamp(mag / _max[b], 0, 1);
        }
        return res;
    }

    public void Dispose() => Stop();
}
