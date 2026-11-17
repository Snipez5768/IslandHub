using System.Windows.Media;
using System.Windows.Threading;
using PeakPill.Core;

namespace PeakPill.Services;

public sealed record SoundOption(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Timer alarm sounds: own synthesised tones (generated once as WAV, no download, no licence issues),
/// the alarm/ring sounds that ship with Windows, or any file the user picks.
/// </summary>
public sealed class SoundService
{
    private const int Rate = 44100;
    private static readonly string Dir = Path.Combine(Storage.Dir, "sounds");
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _stopAfter = new() { Interval = TimeSpan.FromSeconds(60) };
    private bool _loop;

    private static readonly (string Id, string Name, Func<float[]> Make)[] Synth =
    {
        ("synth:soft", L.T("Sanft"), Soft),
        ("synth:marimba", L.T("Marimba"), Marimba),
        ("synth:bell", L.T("Glocke"), Bell),
        ("synth:beep", L.T("Pieps"), Beep),
        ("synth:digital", L.T("Digitalwecker"), Digital),
        ("synth:pulse", L.T("Puls"), Pulse),
    };

    public SoundService()
    {
        _player.MediaEnded += (_, _) =>
        {
            if (!_loop) return;
            _player.Position = TimeSpan.Zero;
            _player.Play();
        };
        _stopAfter.Tick += (_, _) => Stop();
    }

    /// <summary>All selectable sounds (own tones first, then Windows sounds, then the custom file if set).</summary>
    public List<SoundOption> Options(string currentCustom = null)
    {
        var list = new List<SoundOption> { new("none", L.T("Ohne Ton")) };
        list.AddRange(Synth.Select(s => new SoundOption(s.Id, s.Name)));
        try
        {
            var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            foreach (var f in Directory.GetFiles(media, "*.wav")
                         .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(f), @"^(Alarm|Ring)\d+$",
                             System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                         .OrderBy(f => f))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var nice = name.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase)
                    ? L.T("Windows-Alarm ") + int.Parse("0" + new string(name.Where(char.IsDigit).ToArray()))
                    : L.T("Windows-Klingelton ") + int.Parse("0" + new string(name.Where(char.IsDigit).ToArray()));
                list.Add(new SoundOption("file:" + f, nice));
            }
        }
        catch { }
        if (!string.IsNullOrEmpty(currentCustom) && currentCustom.StartsWith("file:") && !list.Any(o => o.Id == currentCustom))
            list.Add(new SoundOption(currentCustom, L.T("Eigene: ") + Path.GetFileNameWithoutExtension(currentCustom[5..])));
        return list;
    }

    public void Play(string id, bool loop)
    {
        Stop();
        var path = PathOf(id);
        if (path == null || !File.Exists(path)) return;
        try
        {
            _loop = loop;
            _player.Open(new Uri(path));
            _player.Volume = 1;
            _player.Play();
            if (loop) _stopAfter.Start(); // never ring forever
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public void Stop()
    {
        _loop = false;
        _stopAfter.Stop();
        try { _player.Stop(); } catch { }
    }

    /// <summary>File behind a sound id (own tones are generated on first use).</summary>
    public static string PathOf(string id)
    {
        if (string.IsNullOrEmpty(id) || id == "none") return null;
        if (id.StartsWith("file:")) return id[5..];
        var s = Synth.FirstOrDefault(x => x.Id == id);
        if (s.Make == null) return null;
        Directory.CreateDirectory(Dir);
        var file = Path.Combine(Dir, id.Replace("synth:", "") + ".wav");
        if (!File.Exists(file)) WriteWav(file, s.Make());
        return file;
    }

    // ------------------------------------------------------------------
    //  Synthesised tones
    // ------------------------------------------------------------------

    private static float[] Buffer(double seconds) => new float[(int)(Rate * seconds)];

    /// <summary>Adds a decaying tone with optional overtones (partial ratio, relative amplitude).</summary>
    private static void Tone(float[] buf, double start, double freq, double length, double amp, double decay, params (double Ratio, double Amp)[] partials)
    {
        int s0 = (int)(start * Rate), n = (int)(length * Rate);
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double t = (double)i / Rate;
            double env = Math.Min(1, t / 0.004) * Math.Exp(-t * decay);                 // 4 ms attack, exponential decay
            double release = Math.Min(1, (length - t) / 0.01);                           // 10 ms fade at the end
            double v = Math.Sin(2 * Math.PI * freq * t);
            foreach (var (r, a) in partials) v += a * Math.Sin(2 * Math.PI * freq * r * t);
            buf[s0 + i] += (float)(amp * env * Math.Max(0, release) * v);
        }
    }

    private static float[] Soft()
    {
        var b = Buffer(2.4);
        Tone(b, 0.00, 1318.5, 1.6, 0.30, 2.6, (2, 0.15));
        Tone(b, 0.32, 1046.5, 1.6, 0.30, 2.6, (2, 0.15));
        Tone(b, 0.64, 1318.5, 1.7, 0.30, 2.4, (2, 0.15));
        return b;
    }

    private static float[] Marimba()
    {
        var b = Buffer(1.8);
        double[] notes = { 523.25, 659.25, 783.99, 1046.5, 783.99, 1046.5 };
        for (int i = 0; i < notes.Length; i++)
            Tone(b, i * 0.14, notes[i], 0.9, 0.34, 7.5, (3.93, 0.25), (10.0, 0.05));
        return b;
    }

    private static float[] Bell()
    {
        var b = Buffer(3.0);
        Tone(b, 0.0, 659.25, 3.0, 0.28, 1.4, (2.0, 0.6), (2.76, 0.4), (5.4, 0.25), (8.93, 0.1));
        Tone(b, 1.0, 659.25, 2.0, 0.22, 1.6, (2.0, 0.6), (2.76, 0.4), (5.4, 0.25));
        return b;
    }

    private static float[] Beep()
    {
        var b = Buffer(1.5);
        for (int g = 0; g < 2; g++)
            for (int i = 0; i < 3; i++)
                Tone(b, g * 0.75 + i * 0.16, 880, 0.09, 0.32, 0, (3, 0.12));
        return b;
    }

    private static float[] Digital()
    {
        var b = Buffer(1.2);
        for (int g = 0; g < 2; g++)
            for (int i = 0; i < 4; i++)
                Tone(b, g * 0.6 + i * 0.09, 2048, 0.05, 0.22, 0, (3, 0.2));
        return b;
    }

    private static float[] Pulse()
    {
        var b = Buffer(1.6);
        for (int i = 0; i < 4; i++)
            Tone(b, i * 0.38, 1000, 0.22, 0.30, 6, (2, 0.1));
        return b;
    }

    private static void WriteWav(string path, float[] samples)
    {
        // normalise to −1 dBFS
        float peak = samples.Max(Math.Abs);
        float k = peak > 0 ? 0.89f / peak : 1f;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int dataLen = samples.Length * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataLen);
        foreach (var s in samples) w.Write((short)Math.Clamp(s * k * 32767, -32768, 32767));
    }
}
