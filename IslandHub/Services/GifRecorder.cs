using System.Diagnostics;
using System.Runtime.InteropServices;
using IslandHub.Core;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;
using WF = System.Windows.Forms;

namespace IslandHub.Services;

/// <summary>
/// Minimal streaming GIF89a encoder (fixed 256-colour palette + LZW). Frames are written as they arrive,
/// so memory stays constant even for long recordings.
/// </summary>
public sealed class GifWriter : IDisposable
{
    private readonly Stream _s;
    private readonly int _w, _h;
    private static readonly byte[] Palette = BuildPalette();
    private static readonly byte[] Lut = BuildLut();

    public GifWriter(Stream s, int width, int height)
    {
        _s = s; _w = width; _h = height;
        Write("GIF89a");
        WriteShort(width); WriteShort(height);
        _s.WriteByte(0xF7);  // global colour table, 8 bit
        _s.WriteByte(0); _s.WriteByte(0);
        _s.Write(Palette);
        // NETSCAPE2.0 loop forever
        _s.Write(new byte[] { 0x21, 0xFF, 0x0B });
        Write("NETSCAPE2.0");
        _s.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
    }

    /// <summary>bgra = 32bpp pixels (stride = width*4).</summary>
    public void AddFrame(byte[] bgra, int delayCs)
    {
        // graphic control extension
        _s.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04 });
        WriteShort(delayCs);
        _s.WriteByte(0); _s.WriteByte(0);
        // image descriptor
        _s.WriteByte(0x2C);
        WriteShort(0); WriteShort(0); WriteShort(_w); WriteShort(_h);
        _s.WriteByte(0);

        var idx = new byte[_w * _h];
        for (int i = 0, p = 0; i < idx.Length; i++, p += 4)
        {
            int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
            idx[i] = Lut[((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)];
        }
        Lzw(idx);
    }

    private readonly int[] _codes = new int[4096 * 256];
    private readonly int[] _stamp = new int[4096 * 256];
    private int _gen;

    private void Lzw(byte[] data)
    {
        const int minCode = 8, clear = 256, eoi = 257;
        _s.WriteByte(minCode);
        var block = new byte[255];
        int blockLen = 0, bitBuf = 0, bitCount = 0;
        int codeSize = minCode + 1, next = eoi + 1;

        void Emit(int code)
        {
            bitBuf |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                block[blockLen++] = (byte)(bitBuf & 0xFF);
                bitBuf >>= 8;
                bitCount -= 8;
                if (blockLen == 255) { _s.WriteByte(255); _s.Write(block, 0, 255); blockLen = 0; }
            }
        }

        _gen++;
        Emit(clear);
        int prefix = data[0];
        for (int i = 1; i < data.Length; i++)
        {
            int c = data[i];
            int key = (prefix << 8) | c;
            if (_stamp[key] == _gen) { prefix = _codes[key]; continue; }
            Emit(prefix);
            if (next < 4096)
            {
                _codes[key] = next;
                _stamp[key] = _gen;
                if (next == (1 << codeSize) && codeSize < 12) codeSize++;
                next++;
            }
            else
            {
                Emit(clear);
                _gen++;
                codeSize = minCode + 1;
                next = eoi + 1;
            }
            prefix = c;
        }
        Emit(prefix);
        Emit(eoi);
        if (bitCount > 0) { block[blockLen++] = (byte)(bitBuf & 0xFF); if (blockLen == 255) { _s.WriteByte(255); _s.Write(block, 0, 255); blockLen = 0; } }
        if (blockLen > 0) { _s.WriteByte((byte)blockLen); _s.Write(block, 0, blockLen); }
        _s.WriteByte(0);
    }

    private static byte[] BuildPalette()
    {
        var p = new byte[256 * 3];
        int n = 0;
        int[] r6 = { 0, 51, 102, 153, 204, 255 }, g7 = { 0, 42, 85, 128, 170, 213, 255 };
        foreach (var r in r6) foreach (var g in g7) foreach (var b in r6) { p[n++] = (byte)r; p[n++] = (byte)g; p[n++] = (byte)b; }
        foreach (var gray in new[] { 28, 44, 70, 230 }) { p[n++] = (byte)gray; p[n++] = (byte)gray; p[n++] = (byte)gray; }
        return p;
    }

    private static byte[] BuildLut()
    {
        var lut = new byte[65536];
        for (int i = 0; i < 65536; i++)
        {
            int r = ((i >> 11) & 31) * 255 / 31, g = ((i >> 5) & 63) * 255 / 63, b = (i & 31) * 255 / 31;
            int best = 0, bestD = int.MaxValue;
            for (int k = 0; k < 256; k++)
            {
                int dr = r - Palette[k * 3], dg = g - Palette[k * 3 + 1], db = b - Palette[k * 3 + 2];
                int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                if (d < bestD) { bestD = d; best = k; }
            }
            lut[i] = (byte)best;
        }
        return lut;
    }

    private void Write(string s) { foreach (var ch in s) _s.WriteByte((byte)ch); }
    private void WriteShort(int v) { _s.WriteByte((byte)(v & 0xFF)); _s.WriteByte((byte)((v >> 8) & 0xFF)); }

    public void Dispose()
    {
        _s.WriteByte(0x3B);
        _s.Flush();
        _s.Dispose();
    }
}

/// <summary>Records a screen region to an animated GIF (10 fps, max 60 s) with a red frame around the area.</summary>
public sealed class GifRecorder
{
    private static GifRecorder _current;
    public static bool IsRecording => _current != null;

    private readonly SD.Rectangle _region;
    private readonly CancellationTokenSource _cts = new();
    private RecordFrame _frame;
    private readonly Stopwatch _clock = new();

    private GifRecorder(SD.Rectangle region) { _region = region; }

    public static Task<string> RecordAsync(SD.Rectangle region)
    {
        if (_current != null) return Task.FromResult<string>(null);
        _current = new GifRecorder(region);
        return _current.Run();
    }

    public static void Stop() => _current?._cts.Cancel();

    private async Task<string> Run()
    {
        var path = Path.Combine(App.Settings.ScreenshotFolder, $"Aufnahme_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.gif");
        Directory.CreateDirectory(App.Settings.ScreenshotFolder);
        _frame = new RecordFrame(_region);
        _frame.Show();
        var ui = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        ui.Tick += (_, _) => App.Island.SetRecording(_clock.Elapsed.ToString(@"m\:ss"));
        ui.Start();
        _clock.Start();
        App.Island.SetRecording("0:00");
        try
        {
            double scale = _region.Width > 960 ? 960.0 / _region.Width : 1.0;
            int w = Math.Max(1, (int)(_region.Width * scale)), h = Math.Max(1, (int)(_region.Height * scale));
            var token = _cts.Token;
            await Task.Run(() =>
            {
                using var gif = new GifWriter(File.Create(path), w, h);
                using var raw = new SD.Bitmap(_region.Width, _region.Height, SDI.PixelFormat.Format32bppRgb);
                using var scaled = new SD.Bitmap(w, h, SDI.PixelFormat.Format32bppRgb);
                var buf = new byte[w * h * 4];
                const int frameMs = 100;
                long last = 0;
                while (!token.IsCancellationRequested && _clock.Elapsed < TimeSpan.FromSeconds(60))
                {
                    using (var g = SD.Graphics.FromImage(raw))
                        g.CopyFromScreen(_region.Left, _region.Top, 0, 0, _region.Size, SD.CopyPixelOperation.SourceCopy);
                    var src = raw;
                    if (scale < 1)
                    {
                        using var g2 = SD.Graphics.FromImage(scaled);
                        g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g2.DrawImage(raw, 0, 0, w, h);
                        src = scaled;
                    }
                    var data = src.LockBits(new SD.Rectangle(0, 0, w, h), SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppRgb);
                    for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * w * 4, w * 4);
                    src.UnlockBits(data);
                    long now = _clock.ElapsedMilliseconds;
                    gif.AddFrame(buf, (int)Math.Max(2, Math.Round((now - last) / 10.0)));
                    last = now;
                    int sleep = frameMs - (int)(_clock.ElapsedMilliseconds - now);
                    if (sleep > 0) token.WaitHandle.WaitOne(sleep);
                }
            });
            return path;
        }
        catch (Exception ex) { App.Log(ex); return null; }
        finally
        {
            ui.Stop();
            _frame.Close();
            App.Island.SetRecording(null);
            _current = null;
        }
    }

    /// <summary>Click-through red frame drawn just outside the recorded region.</summary>
    private sealed class RecordFrame : WF.Form
    {
        private readonly SD.Rectangle _r;

        public RecordFrame(SD.Rectangle r)
        {
            _r = r;
            FormBorderStyle = WF.FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = WF.FormStartPosition.Manual;
            AutoScaleMode = WF.AutoScaleMode.None;
            BackColor = SD.Color.Magenta;
            TransparencyKey = SD.Color.Magenta;
            Bounds = SD.Rectangle.Inflate(r, 3, 3);
        }

        protected override WF.CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80000 | 0x80; // transparent, layered, toolwindow
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e) { base.OnLoad(e); Bounds = SD.Rectangle.Inflate(_r, 3, 3); }

        protected override void WndProc(ref WF.Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED) { Bounds = SD.Rectangle.Inflate(_r, 3, 3); return; }
            base.WndProc(ref m);
        }

        protected override void OnPaint(WF.PaintEventArgs e)
        {
            using var pen = new SD.Pen(SD.Color.FromArgb(255, 255, 69, 58), 3) { DashPattern = new[] { 4f, 2f } };
            e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }
    }
}
