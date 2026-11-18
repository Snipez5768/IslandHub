using System.Windows;
using System.Windows.Media;
using PeakPill.Core;
using PeakPill.Services;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace PeakPill.UI;

/// <summary>Runs the whole screenshot flow: capture bar → capture → clipboard → floating preview / editor.</summary>
public static class ScreenshotController
{
    private static bool _busy;

    public static async void Start(bool liveText, bool explain = false)
    {
        if (GifRecorder.IsRecording) { GifRecorder.Stop(); return; }
        if (_busy) return;
        _busy = true;
        try
        {
            App.Island.SetCaptureHidden(true);
            PreviewWindow.CloseCurrent();
            await Task.Delay(160);

            var result = await RunOverlayAsync();
            App.Island.SetCaptureHidden(false);
            if (result == null) return;

            switch (result.Mode)
            {
                case CaptureMode.Picker:
                    var c = result.PickedColor;
                    var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                    App.Hub.Clipboard.AddText(hex, L.T("Farbpipette"));
                    App.Island.ShowHint("eyedropper", hex, Frozen(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B))), check: true);
                    return;

                case CaptureMode.Gif:
                    var gif = await GifRecorder.RecordAsync(result.Region);
                    if (gif != null)
                    {
                        var sc = new System.Collections.Specialized.StringCollection { gif };
                        try { Clipboard.SetFileDropList(sc); } catch { }
                        App.Hub.Shelf.Add(new[] { gif });
                        App.Island.ShowPeek(new PeekInfo { Important = true, Icon = "record", GlyphBrush = Brushes.White, Title = L.T("GIF gesichert"), Text = Path.GetFileName(gif), TimeoutMs = 5000 }
                            .Action(L.T("Zeigen"), () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{gif}\""), true));
                    }
                    return;
            }

            SD.Bitmap bmp = result.Bitmap;
            if (bmp == null && result.DelaySeconds > 0)
            {
                for (int s = result.DelaySeconds; s > 0; s--)
                {
                    App.Island.ShowHint("timer", s.ToString(), (Brush)Application.Current.FindResource("Orange"), key: "countdown");
                    await Task.Delay(1000);
                }
                App.Island.SetCaptureHidden(true);
                await Task.Delay(150);
                bmp = ScreenshotService.Capture(result.Region);
                App.Island.SetCaptureHidden(false);
            }
            if (bmp == null) return;

            App.Settings.LastRegion = $"{result.Region.X},{result.Region.Y},{result.Region.Width},{result.Region.Height}";
            ClipItem item;
            System.Windows.Media.Imaging.BitmapSource src;
            using (bmp) { src = ScreenshotService.ToBitmapSource(bmp); item = App.Hub.Clipboard.AddImage(src, true); }

            if (explain) { App.Island.ExplainShot(item.ImageFile); return; } // 14 (2026-11-18): the KI says what it is
            if (liveText) { EditorWindow.Open(item, liveText: true); return; }
            // the picture shrinks and flies into the island (2026-11-17, no flash) – the preview comes after it landed
            await App.Island.FlyShot(src, result.Region);

            if (App.Settings.ScreenshotAutoSave)
            {
                try { ScreenshotService.SaveCopy(item.ImageFile, App.Settings.ScreenshotFolder); } catch (Exception ex) { App.Log(ex); }
            }
            if (App.Settings.ScreenshotAsk) PreviewWindow.Show(item, WF.Screen.FromRectangle(result.Region));
            else App.Island.ShowHint("camera", L.T("Kopiert"), null, check: true);
        }
        catch (Exception ex) { App.Log(ex); }
        finally
        {
            App.Island.SetCaptureHidden(false);
            _busy = false;
        }
    }

    /// <summary>Repeats the last captured area without showing the overlay.</summary>
    public static void RepeatLastRegion(CaptureSession session, Action<CaptureResult> done)
    {
        var p = (App.Settings.LastRegion ?? "").Split(',');
        if (p.Length != 4 || !int.TryParse(p[0], out var x) || !int.TryParse(p[1], out var y) || !int.TryParse(p[2], out var w) || !int.TryParse(p[3], out var h)) return;
        done(new CaptureResult { Mode = CaptureMode.Area, Region = new SD.Rectangle(x, y, w, h), DelaySeconds = Math.Max(1, session.DelaySeconds) });
    }

    private static Task<CaptureResult> RunOverlayAsync()
    {
        var tcs = new TaskCompletionSource<CaptureResult>();
        var session = new CaptureSession { DelaySeconds = App.Settings.ScreenshotTimer };
        session.Windows.AddRange(ScreenshotService.EnumerateWindows());
        var screens = WF.Screen.AllScreens;
        var shots = screens.Select(s => ScreenshotService.Capture(s.Bounds)).ToList();
        var forms = new List<CaptureOverlay>();
        CaptureToolbar bar = null;
        bool done = false;

        void Finish(CaptureResult r)
        {
            if (done) return;
            done = true;
            App.Settings.ScreenshotTimer = session.DelaySeconds;
            try { bar?.Close(); } catch { }
            foreach (var f in forms) { try { f.Close(); f.Dispose(); } catch { } }
            foreach (var b in shots) b.Dispose();
            tcs.TrySetResult(r);
        }

        for (int i = 0; i < screens.Length; i++)
        {
            var f = new CaptureOverlay(screens[i].Bounds, shots[i], session);
            f.Completed += Finish;
            f.Cancelled += () => Finish(null);
            forms.Add(f);
            f.Show();
        }

        var pos = WF.Cursor.Position;
        int idx = Math.Max(0, Array.FindIndex(screens, s => s.Bounds.Contains(pos)));
        forms[idx].Activate();
        bar = new CaptureToolbar(session, screens[idx], shots[idx]);
        bar.Cancelled += () => Finish(null);
        bar.RepeatLast += () => RepeatLastRegion(session, Finish);
        bar.Show(); // shown last (and kept on top) so it floats above the overlays without taking focus
        return tcs.Task;
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
