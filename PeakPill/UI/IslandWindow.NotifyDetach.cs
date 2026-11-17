using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// User wish 2026-11-17: a notification is shown, you go into its app (Windows toast, taskbar, "Öffnen") – the card
/// lets go of the island and drops away, and the app's messages count as read (no badge, no banner any more).
/// </summary>
public partial class IslandWindow
{
    /// <summary>The notification the banner shows right now.</summary>
    private IslandNotification _peekNotif;

    private void WireNotifyDetach()
    {
        H.Foreground.Changed += h => Ui.Run(() => OnForegroundForNotifications(h));
    }

    private void OnForegroundForNotifications(IntPtr h)
    {
        if (h == IntPtr.Zero || H.Foreground.IsOwn(h)) return;
        var unread = H.Notifications.Items.Where(n => n.Unread).ToList();
        bool banner = _state == State.Peek && _peek is { Key: "notif" } && _peekNotif != null;
        if (unread.Count == 0 && !banner) return;
        var app = AppKeys(h);
        var aumid = WindowAppId(h);
        if (app.Length == 0 && aumid == null) return;

        bool any = false;
        foreach (var n in unread.Where(n => FromApp(n, app, aumid))) { n.Unread = false; any = true; }
        if (any) UpdateNotifyUi();
        if (banner && FromApp(_peekNotif, app, aumid)) DetachPeek();
    }

    // ---------- the window's own app id (2026-11-18): the same id Windows files its notifications under ----------

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PropKey key);
        int GetValue(ref PropKey key, out PropVariant value);
        int SetValue(ref PropKey key, ref PropVariant value);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropKey { public Guid Fmt; public uint Pid; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant { [FieldOffset(0)] public ushort Vt; [FieldOffset(8)] public IntPtr Ptr; }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, System.Text.StringBuilder id);

    /// <summary>The window's AppUserModelID (set by the app, by the UWP frame, or the packaged process's), else null.</summary>
    private static string WindowAppId(IntPtr h)
    {
        try
        {
            var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
            if (SHGetPropertyStoreForWindow(h, ref iid, out var store) == 0 && store != null)
            {
                var key = new PropKey { Fmt = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Pid = 5 };
                if (store.GetValue(ref key, out var v) == 0)
                {
                    string s = v.Vt == 31 ? Marshal.PtrToStringUni(v.Ptr) : null; // VT_LPWSTR
                    PropVariantClear(ref v);
                    Marshal.ReleaseComObject(store);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                else Marshal.ReleaseComObject(store);
            }
        }
        catch { }
        try
        {
            Native.GetWindowThreadProcessId(h, out var pid);
            using var p = Process.GetProcessById((int)pid);
            uint len = 512;
            var sb = new System.Text.StringBuilder((int)len);
            if (GetApplicationUserModelId(p.Handle, ref len, sb) == 0) return sb.ToString(); // packaged apps (WhatsApp, Teams …)
        }
        catch { }
        return null;
    }

    /// <summary>Names the foreground app goes by: its process name and its description ("WhatsApp", "Discord").</summary>
    private static string[] AppKeys(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out var pid);
        string proc = null;
        try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }
        return new[] { Norm(proc), Norm(ForegroundTracker.AppNameOf(h)) }
            .Where(k => k.Length >= 3 && k is not ("applicationframehost" or "explorer" or "peakpill")).Distinct().ToArray();
    }

    private static bool FromApp(IslandNotification n, string[] keys, string aumid = null)
    {
        // the exact id first – reliable for every app; the names are the fallback
        if (aumid != null && !string.IsNullOrEmpty(n.AppId) && string.Equals(n.AppId, aumid, StringComparison.OrdinalIgnoreCase)) return true;
        var name = Norm(n.AppName);
        var hay = name + "|" + Norm(n.AppId);
        return keys.Any(k => hay.Contains(k) || name.Length >= 3 && k.Contains(name));
    }

    private static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The card comes loose from the island and falls away while the island shrinks back to the pill.</summary>
    private void DetachPeek()
    {
        _peekNotif = null;
        if (S.Animations == AnimationLevel.Aus || Island.ActualWidth < 1) { EndPeek(); return; }
        BitmapSource shot = null;
        Point at = default;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(Island.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(Island.ActualHeight * dpi.DpiScaleY),
                                             dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(Island), null, new Rect(0, 0, Island.ActualWidth, Island.ActualHeight));
            rtb.Render(dv);
            rtb.Freeze();
            shot = rtb;
            at = Island.TranslatePoint(new Point(0, 0), Root);
        }
        catch { }
        EndPeek();
        if (shot == null) return;

        var img = new Image { Source = shot, Width = Island.ActualWidth, Height = Island.ActualHeight, RenderTransformOrigin = new Point(0.5, 0) };
        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        img.RenderTransform = new TransformGroup { Children = { scale, move } };
        Canvas.SetLeft(img, at.X);
        Canvas.SetTop(img, at.Y);
        FlyLayer.Children.Add(img);

        // a short tug upwards (still stuck), then it lets go and drops, getting smaller and fading
        var drop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        drop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        drop.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromPercent(0.18), new SineEase { EasingMode = EasingMode.EaseOut }));
        drop.KeyFrames.Add(new EasingDoubleKeyFrame(46, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseIn }));
        move.BeginAnimation(TranslateTransform.YProperty, drop);
        var shrink = new DoubleAnimation(1, 0.88, TimeSpan.FromMilliseconds(520)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(380)) { BeginTime = TimeSpan.FromMilliseconds(140) };
        fade.Completed += (_, _) => FlyLayer.Children.Remove(img);
        img.BeginAnimation(OpacityProperty, fade);
    }
}
