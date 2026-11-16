using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using PeakPill.Core;
using SD = System.Drawing;
using SD2 = System.Drawing.Drawing2D;
using SDI = System.Drawing.Imaging;
using WF = System.Windows.Forms;
using WinImg = Windows.Graphics.Imaging;

namespace PeakPill.Services;

public enum CaptureMode { Area, Window, Screen, Freeform, Gif, Picker }

public sealed class CaptureResult
{
    public SD.Bitmap Bitmap;            // already cropped (null for Gif / delayed / picker)
    public SD.Rectangle Region;         // screen pixels
    public CaptureMode Mode;
    public int DelaySeconds;
    public SD.Color PickedColor;
}

/// <summary>Shared state between the per-monitor overlays and the toolbar.</summary>
public sealed class CaptureSession
{
    public CaptureMode Mode = CaptureMode.Area;
    public int DelaySeconds;
    public readonly List<(SD.Rectangle Rect, IntPtr Hwnd)> Windows = new();
    public event Action Changed;
    public void Notify() => Changed?.Invoke();
}

public static class ScreenshotService
{
    public static SD.Bitmap Capture(SD.Rectangle r)
    {
        var b = new SD.Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), SDI.PixelFormat.Format32bppRgb);
        using var g = SD.Graphics.FromImage(b);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, SD.CopyPixelOperation.SourceCopy);
        return b;
    }

    public static BitmapSource ToBitmapSource(SD.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, SDI.ImageFormat.Png);
        ms.Position = 0;
        var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    public static string SaveCopy(string sourcePng, string folder)
    {
        Directory.CreateDirectory(folder);
        var dest = Path.Combine(folder, $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
        int n = 1;
        while (File.Exists(dest)) dest = Path.Combine(folder, $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{n++}.png");
        File.Copy(sourcePng, dest);
        return dest;
    }

    public static void SavePng(BitmapSource img, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ---------- top-level windows (for "Fenster" mode) ----------

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT r, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

    public static List<(SD.Rectangle, IntPtr)> EnumerateWindows()
    {
        var list = new List<(SD.Rectangle, IntPtr)>();
        uint me = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == me) return true;
            if (DwmGetWindowAttribute(h, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var cls = Native.ClassNameOf(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (GetWindowTextLength(h) == 0 && cls != "Windows.UI.Core.CoreWindow") return true;
            if (DwmGetWindowAttribute(h, 9, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) != 0) Native.GetWindowRect(h, out r);
            var rect = SD.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (rect.Width > 40 && rect.Height > 30) list.Add((rect, h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---------- QR detection (ZXing, offline) ----------

    public static string DecodeQr(BitmapSource src)
    {
        try
        {
            var conv = new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            var reader = new ZXing.BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = { TryHarder = true, PossibleFormats = new List<ZXing.BarcodeFormat> { ZXing.BarcodeFormat.QR_CODE } }
            };
            var res = reader.Decode(new ZXing.RGBLuminanceSource(px, w, h, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32));
            return res?.Text;
        }
        catch { return null; }
    }
}

/// <summary>Snipping-tool style overlay (one per monitor) – area, window, screen, freeform, GIF region and colour picker.</summary>
internal sealed class CaptureOverlay : WF.Form
{
    private readonly SD.Rectangle _target;
    private readonly SD.Bitmap _shot;
    private readonly SD.Bitmap _dimmed;
    private readonly CaptureSession _session;
    private SD.Point _start, _mouse;
    private SD.Rectangle _sel;
    private bool _dragging;
    private readonly List<SD.Point> _lasso = new();
    private SD.Rectangle _hover;
    private static readonly SD.Font LabelFont = new("Segoe UI", 9.5f, SD.FontStyle.Bold);
    private static readonly SD.Color Blue = SD.Color.FromArgb(255, 10, 132, 255);

    public event Action<CaptureResult> Completed;
    public event Action Cancelled;

    public CaptureOverlay(SD.Rectangle bounds, SD.Bitmap shot, CaptureSession session)
    {
        _target = bounds;
        _shot = shot;
        _session = session;
        FormBorderStyle = WF.FormBorderStyle.None;
        StartPosition = WF.FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = WF.AutoScaleMode.None;
        KeyPreview = true;
        Cursor = WF.Cursors.Cross;
        DoubleBuffered = true;
        SetStyle(WF.ControlStyles.AllPaintingInWmPaint | WF.ControlStyles.UserPaint | WF.ControlStyles.OptimizedDoubleBuffer, true);
        Bounds = bounds;

        _dimmed = new SD.Bitmap(shot.Width, shot.Height, SDI.PixelFormat.Format32bppRgb);
        using (var g = SD.Graphics.FromImage(_dimmed))
        {
            g.DrawImageUnscaled(shot, 0, 0);
            using var br = new SD.SolidBrush(SD.Color.FromArgb(105, 0, 0, 0));
            g.FillRectangle(br, 0, 0, shot.Width, shot.Height);
        }
        _session.Changed += OnSessionChanged;
    }

    private void OnSessionChanged()
    {
        _sel = SD.Rectangle.Empty;
        _lasso.Clear();
        _hover = SD.Rectangle.Empty;
        Cursor = _session.Mode is CaptureMode.Window or CaptureMode.Screen ? WF.Cursors.Hand : WF.Cursors.Cross;
        UpdateHover(PointToClient(WF.Cursor.Position));
        Invalidate();
    }

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); Bounds = _target; }

    protected override void WndProc(ref WF.Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) { Bounds = _target; return; }
        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(WF.PaintEventArgs e) { }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImage(_dimmed, e.ClipRectangle, e.ClipRectangle, SD.GraphicsUnit.Pixel);
        g.SmoothingMode = SD2.SmoothingMode.AntiAlias;

        switch (_session.Mode)
        {
            case CaptureMode.Area:
            case CaptureMode.Gif:
                if (_sel.Width > 0 && _sel.Height > 0)
                {
                    g.DrawImage(_shot, _sel, _sel, SD.GraphicsUnit.Pixel);
                    var color = _session.Mode == CaptureMode.Gif ? SD.Color.FromArgb(255, 255, 69, 58) : SD.Color.White;
                    using var pen = new SD.Pen(color, 1.5f) { DashPattern = new[] { 4f, 3f } };
                    g.DrawRectangle(pen, _sel);
                    DrawLabel(g, $"{_sel.Width} × {_sel.Height}", _sel.Left, _sel.Top - 26);
                }
                break;
            case CaptureMode.Window:
            case CaptureMode.Screen:
                if (_hover.Width > 0)
                {
                    g.DrawImage(_shot, _hover, _hover, SD.GraphicsUnit.Pixel);
                    using var tint = new SD.SolidBrush(SD.Color.FromArgb(40, 10, 132, 255));
                    g.FillRectangle(tint, _hover);
                    using var pen = new SD.Pen(Blue, 2);
                    g.DrawRectangle(pen, SD.Rectangle.Inflate(_hover, -1, -1));
                }
                break;
            case CaptureMode.Freeform:
                if (_lasso.Count > 2)
                {
                    using var path = new SD2.GraphicsPath();
                    path.AddLines(_lasso.ToArray());
                    using (var tb = new SD.TextureBrush(_shot)) g.FillPath(tb, path);
                    using var pen = new SD.Pen(SD.Color.White, 1.6f);
                    g.DrawPath(pen, path);
                }
                break;
            case CaptureMode.Picker:
                DrawLoupe(g);
                break;
        }
    }

    private static void DrawLabel(SD.Graphics g, string text, float x, float y)
    {
        var size = g.MeasureString(text, LabelFont);
        if (y < 6) y = 6;
        var r = new SD.RectangleF(x, y, size.Width + 12, size.Height + 6);
        using var path = RoundRect(r, 8);
        using var bg = new SD.SolidBrush(SD.Color.FromArgb(225, 28, 28, 30));
        g.FillPath(bg, path);
        g.DrawString(text, LabelFont, SD.Brushes.White, x + 6, y + 3);
    }

    private void DrawLoupe(SD.Graphics g)
    {
        if (!ClientRectangle.Contains(_mouse)) return;
        const int n = 11, z = 11;
        int size = n * z;
        int lx = _mouse.X + 24, ly = _mouse.Y + 24;
        if (lx + size > Width) lx = _mouse.X - 24 - size;
        if (ly + size + 30 > Height) ly = _mouse.Y - 24 - size - 30;
        var circle = new SD.Rectangle(lx, ly, size, size);
        using (var clip = new SD2.GraphicsPath())
        {
            clip.AddEllipse(circle);
            var state = g.Save();
            g.SetClip(clip);
            g.InterpolationMode = SD2.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = SD2.PixelOffsetMode.Half;
            g.DrawImage(_shot, circle, new SD.Rectangle(_mouse.X - n / 2, _mouse.Y - n / 2, n, n), SD.GraphicsUnit.Pixel);
            using var cell = new SD.Pen(SD.Color.FromArgb(220, 255, 255, 255), 1.5f);
            g.DrawRectangle(cell, lx + (n / 2) * z, ly + (n / 2) * z, z, z);
            g.Restore(state);
        }
        using var ring = new SD.Pen(SD.Color.FromArgb(230, 255, 255, 255), 2.5f);
        g.DrawEllipse(ring, circle);
        var c = PixelAt(_mouse);
        DrawLabel(g, $"#{c.R:X2}{c.G:X2}{c.B:X2}", lx + size / 2f - 34, ly + size + 6);
    }

    private SD.Color PixelAt(SD.Point p)
    {
        int x = Math.Clamp(p.X, 0, _shot.Width - 1), y = Math.Clamp(p.Y, 0, _shot.Height - 1);
        return _shot.GetPixel(x, y);
    }

    private static SD2.GraphicsPath RoundRect(SD.RectangleF r, float rad)
    {
        var p = new SD2.GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void UpdateHover(SD.Point local)
    {
        var old = _hover;
        if (_session.Mode == CaptureMode.Screen) _hover = new SD.Rectangle(0, 0, Width, Height);
        else if (_session.Mode == CaptureMode.Window)
        {
            var screenPt = new SD.Point(local.X + _target.Left, local.Y + _target.Top);
            _hover = SD.Rectangle.Empty;
            foreach (var (rect, _) in _session.Windows)
            {
                if (!rect.Contains(screenPt)) continue;
                var r = rect;
                r.Offset(-_target.Left, -_target.Top);
                r.Intersect(new SD.Rectangle(0, 0, Width, Height));
                _hover = r;
                break;
            }
        }
        else _hover = SD.Rectangle.Empty;
        if (old != _hover) Invalidate();
    }

    protected override void OnMouseDown(WF.MouseEventArgs e)
    {
        if (e.Button == WF.MouseButtons.Right) { Cancelled?.Invoke(); return; }
        if (e.Button != WF.MouseButtons.Left) return;
        switch (_session.Mode)
        {
            case CaptureMode.Window:
            case CaptureMode.Screen:
                if (_hover.Width > 2) Finish(_hover);
                return;
            case CaptureMode.Picker:
                Completed?.Invoke(new CaptureResult { Mode = CaptureMode.Picker, PickedColor = PixelAt(e.Location) });
                return;
        }
        _dragging = true;
        _start = e.Location;
        _sel = SD.Rectangle.Empty;
        _lasso.Clear();
        _lasso.Add(e.Location);
        Invalidate();
    }

    protected override void OnMouseMove(WF.MouseEventArgs e)
    {
        var prev = _mouse;
        _mouse = e.Location;
        if (_session.Mode == CaptureMode.Picker)
        {
            Invalidate(new SD.Rectangle(Math.Min(prev.X, _mouse.X) - 200, Math.Min(prev.Y, _mouse.Y) - 200, Math.Abs(prev.X - _mouse.X) + 400, Math.Abs(prev.Y - _mouse.Y) + 400));
            return;
        }
        if (!_dragging) { UpdateHover(e.Location); return; }
        if (_session.Mode == CaptureMode.Freeform)
        {
            _lasso.Add(e.Location);
            Invalidate();
            return;
        }
        var old = _sel;
        _sel = SD.Rectangle.FromLTRB(Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y), Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
        _sel.Intersect(new SD.Rectangle(0, 0, _shot.Width, _shot.Height));
        var dirty = SD.Rectangle.Union(old, _sel);
        dirty.Inflate(6, 40);
        Invalidate(dirty);
    }

    protected override void OnMouseUp(WF.MouseEventArgs e)
    {
        if (!_dragging || e.Button != WF.MouseButtons.Left) return;
        _dragging = false;
        if (_session.Mode == CaptureMode.Freeform)
        {
            if (_lasso.Count < 5) { _lasso.Clear(); Invalidate(); return; }
            FinishLasso();
            return;
        }
        if (_sel.Width < 4 || _sel.Height < 4) { _sel = SD.Rectangle.Empty; Invalidate(); return; }
        Finish(_sel);
    }

    private void Finish(SD.Rectangle local)
    {
        var screen = local;
        screen.Offset(_target.Left, _target.Top);
        var res = new CaptureResult { Mode = _session.Mode, Region = screen, DelaySeconds = _session.DelaySeconds };
        if (_session.Mode != CaptureMode.Gif && _session.DelaySeconds == 0)
            res.Bitmap = _shot.Clone(local, SDI.PixelFormat.Format32bppArgb);
        Completed?.Invoke(res);
    }

    private void FinishLasso()
    {
        using var path = new SD2.GraphicsPath();
        path.AddLines(_lasso.ToArray());
        path.CloseFigure();
        var b = SD.Rectangle.Round(path.GetBounds());
        b.Intersect(new SD.Rectangle(0, 0, _shot.Width, _shot.Height));
        if (b.Width < 4 || b.Height < 4) { _lasso.Clear(); Invalidate(); return; }
        var bmp = new SD.Bitmap(b.Width, b.Height, SDI.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD2.SmoothingMode.AntiAlias;
            g.Clear(SD.Color.Transparent);
            using var tb = new SD.TextureBrush(_shot);
            tb.TranslateTransform(-b.X, -b.Y);
            using var m = new SD2.Matrix();
            m.Translate(-b.X, -b.Y);
            path.Transform(m);
            g.FillPath(tb, path);
        }
        var screen = b;
        screen.Offset(_target.Left, _target.Top);
        Completed?.Invoke(new CaptureResult { Mode = CaptureMode.Freeform, Region = screen, Bitmap = bmp });
    }

    protected override void OnKeyDown(WF.KeyEventArgs e)
    {
        if (e.KeyCode == WF.Keys.Escape) Cancelled?.Invoke();
        else if (e.KeyCode == WF.Keys.Enter) Finish(new SD.Rectangle(0, 0, _shot.Width, _shot.Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _session.Changed -= OnSessionChanged; _dimmed.Dispose(); }
        base.Dispose(disposing);
    }
}

public sealed record OcrWord(string Text, System.Windows.Rect Box, int Line);

/// <summary>Text recognition with the OCR engine built into Windows (offline).</summary>
public static class OcrService
{
    public static async Task<string> RecognizeAsync(string pngPath)
    {
        var words = await RecognizeWordsAsync(pngPath);
        return JoinWords(words);
    }

    public static string JoinWords(IEnumerable<OcrWord> words)
    {
        var sb = new StringBuilder();
        int line = -1;
        foreach (var w in words)
        {
            if (line != -1) sb.Append(w.Line != line ? Environment.NewLine : " ");
            sb.Append(w.Text);
            line = w.Line;
        }
        return sb.ToString().Trim();
    }

    /// <summary>Words with bounding boxes in original image pixels, in reading order.</summary>
    public static async Task<List<OcrWord>> RecognizeWordsAsync(string pngPath)
    {
        var file = await StorageFile.GetFileFromPathAsync(pngPath);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await WinImg.BitmapDecoder.CreateAsync(stream);

        uint w = decoder.PixelWidth, h = decoder.PixelHeight;
        double scale = Math.Max(w, h) < 1200 ? 2.0 : 1.0;
        uint max = OcrEngine.MaxImageDimension;
        if (Math.Max(w, h) * scale > max) scale = (double)max / Math.Max(w, h);

        var transform = new WinImg.BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, w * scale),
            ScaledHeight = (uint)Math.Max(1, h * scale),
            InterpolationMode = WinImg.BitmapInterpolationMode.Fant
        };
        using var bmp = await decoder.GetSoftwareBitmapAsync(WinImg.BitmapPixelFormat.Bgra8, WinImg.BitmapAlphaMode.Premultiplied,
            transform, WinImg.ExifOrientationMode.IgnoreExifOrientation, WinImg.ColorManagementMode.DoNotColorManage);

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine == null) throw new InvalidOperationException("Keine OCR-Sprache installiert (Windows-Einstellungen → Sprache).");

        var result = await engine.RecognizeAsync(bmp);
        var list = new List<OcrWord>();
        int li = 0;
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                list.Add(new OcrWord(word.Text, new System.Windows.Rect(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale), li));
            }
            li++;
        }
        return list;
    }
}
