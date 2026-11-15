using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SD = System.Drawing;

namespace IslandHub.Core;

/// <summary>Remembers the last foreground window that does not belong to IslandHub (paste target).</summary>
public sealed class ForegroundTracker : IDisposable
{
    private IntPtr _hook;
    private Native.WinEventDelegate _del;
    private readonly uint _myPid = (uint)Environment.ProcessId;
    private static readonly Dictionary<uint, string> NameCache = new();

    public IntPtr LastExternal { get; private set; }
    public event Action<IntPtr> Changed;

    public void Start()
    {
        _del = (_, _, hwnd, _, _, _, _) => Update(hwnd);
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _del, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        Update(Native.GetForegroundWindow());
    }

    private void Update(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid != _myPid) LastExternal = h;
        Changed?.Invoke(h);
    }

    public bool IsOwn(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out var pid);
        return pid == _myPid;
    }

    public static string AppNameOf(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(h, out var pid);
        if (NameCache.TryGetValue(pid, out var n)) return n;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string name = p.ProcessName;
            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 32) name = desc;
            }
            catch { /* elevated / protected process */ }
            NameCache[pid] = name;
            return name;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }
}

/// <summary>Low-level keyboard hook that reports physical Ctrl+V presses (used by the queue mode).</summary>
/// <summary>
/// Notices Ctrl+V anywhere (queue mode). Runs on its OWN thread with its own message loop: Windows silently drops a
/// low-level hook whose thread answers too slowly – on the busy UI thread (animations, layout) that happened, and the
/// queue stopped working without any error.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private IntPtr _hook;
    private Native.LowLevelKeyboardProc _proc;
    private bool _vWithCtrl;
    private Thread _thread;
    private uint _threadId;

    public event Action PasteDetected;
    public bool Installed => _thread != null;
    /// <summary>Only for the queue test, which sends Ctrl+V itself.</summary>
    public bool AcceptInjected { get; set; }

    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    private const uint WM_QUIT = 0x0012;

    public void Install()
    {
        if (_thread != null) return;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _proc = Callback;
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } // the hook is called from this loop
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "IslandHub paste hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(2000);
    }

    public void Uninstall()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread = null;
    }

    private IntPtr Callback(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(l);
            if (((k.flags & Native.LLKHF_INJECTED) == 0 || AcceptInjected) && k.vkCode == Native.VK_V)
            {
                int msg = (int)w;
                bool ctrl = (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
                bool alt = (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
                if ((msg == Native.WM_KEYDOWN) && ctrl && !alt) _vWithCtrl = true;
                else if ((msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP) && _vWithCtrl)
                {
                    _vWithCtrl = false;
                    PasteDetected?.Invoke();
                }
            }
        }
        return Native.CallNextHookEx(_hook, code, w, l);
    }

    public void Dispose() => Uninstall();
}

/// <summary>
/// Low-level mouse hook used only while the island is open: reports clicks that land outside IslandHub,
/// so the island closes immediately – even in the middle of its opening animation.
/// </summary>
public sealed class OutsideClickHook : IDisposable
{
    private IntPtr _hook;
    private Native.LowLevelKeyboardProc _proc; // same signature as the mouse proc
    private readonly uint _myPid = (uint)Environment.ProcessId;

    public event Action OutsideClick;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            int msg = (int)w;
            if (msg is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(l);
                var hit = Native.WindowFromPoint(info.pt);
                var root = hit == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hit, 2 /* GA_ROOT */);
                Native.GetWindowThreadProcessId(root, out var pid);
                // transparent parts of the island window are click-through, so a hit on our process = a hit on visible island UI / popups
                if (pid != _myPid) OutsideClick?.Invoke();
            }
        }
        return Native.CallNextHookEx(_hook, code, w, l);
    }

    public void Dispose() => Uninstall();
}

public static class PasteHelper
{
    public static async Task PasteInto(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && Native.IsWindow(hwnd)) Native.SetForegroundWindow(hwnd);
        await Task.Delay(90);
        var inputs = new[]
        {
            Key(Native.VK_CONTROL, false), Key(Native.VK_V, false),
            Key(Native.VK_V, true), Key(Native.VK_CONTROL, true)
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    /// <summary>Presses a shortcut system-wide (e.g. Discord's own keybind for screen sharing).</summary>
    public static void SendCombo(HotkeyGesture g)
    {
        var mods = new List<ushort>();
        if (g.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) mods.Add(0x11);
        if (g.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) mods.Add(0x12);
        if (g.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) mods.Add(0x10);
        if (g.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows)) mods.Add(0x5B);
        var vk = (ushort)System.Windows.Input.KeyInterop.VirtualKeyFromKey(g.Key);
        var inputs = mods.Select(m => Key(m, false))
            .Append(Key(vk, false)).Append(Key(vk, true))
            .Concat(mods.AsEnumerable().Reverse().Select(m => Key(m, true))).ToArray();
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    private static Native.INPUT Key(ushort vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } }
    };
}

public static class ShellIcon
{
    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };

    public static bool IsImage(string path) => ImageExt.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static ImageSource Get(string path)
    {
        try
        {
            if (File.Exists(path) && IsImage(path))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path);
                bi.DecodePixelWidth = 140;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
        }
        catch { /* fall back to the shell icon */ }

        var info = new Native.SHFILEINFO();
        var r = Native.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
        if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var bs = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        finally { Native.DestroyIcon(info.hIcon); }
    }
}

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Task-Manager › Autostart keeps its own on/off flag per entry here (first byte odd = disabled)
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (on)
            {
                k.SetValue("IslandHub", $"\"{Environment.ProcessPath}\" --autostart");
                // switched on here = really on, even if it was once disabled in the Task-Manager
                using var a = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
                a?.DeleteValue("IslandHub", false);
            }
            else k.DeleteValue("IslandHub", false);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>True when Windows will really start IslandHub at sign-in (entry present and not disabled in the Task-Manager).</summary>
    public static bool IsOn()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            if (k?.GetValue("IslandHub") is not string) return false;
            using var a = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return !(a?.GetValue("IslandHub") is byte[] { Length: > 0 } b && (b[0] & 1) == 1);
        }
        catch { return false; }
    }

    /// <summary>Keeps the entry pointing at this exe (e.g. after IslandHub was moved).</summary>
    public static void Refresh()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            var want = $"\"{Environment.ProcessPath}\" --autostart";
            if (k?.GetValue("IslandHub") is string cur && cur != want) k.SetValue("IslandHub", want);
        }
        catch (Exception ex) { App.Log(ex); }
    }
}

public static class ThemeManager
{
    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Blau", "#0A84FF"), ("Türkis", "#64D2FF"), ("Grün", "#30D158"), ("Gelb", "#FFD60A"),
        ("Orange", "#FF9F0A"), ("Rot", "#FF453A"), ("Pink", "#FF375F"), ("Lila", "#BF5AF2")
    };

    public static void Apply()
    {
        var s = App.Settings;
        var r = Application.Current.Resources;
        var accent = Parse(s.Accent, Color.FromRgb(0x0A, 0x84, 0xFF));
        r["AccentColor"] = accent;
        r["Accent"] = Frozen(new SolidColorBrush(accent));
        r["AccentGlass"] = Frozen(new SolidColorBrush(Color.FromArgb(0xD9, accent.R, accent.G, accent.B)));

        // bubble = what the side bubbles and the sticky neck wear, so they never look darker/lighter than the island
        Brush bg, border, bubble;
        switch (s.Theme)
        {
            case IslandTheme.Graphit:
                bg = bubble = Solid("#FF1C1C1E"); border = Solid("#1FFFFFFF"); break;
            case IslandTheme.Mitternacht:
                bg = Gradient(("#FF0B1024", 0), ("#FF15112E", 1)); bubble = Solid("#FF15112E"); border = Solid("#00000000"); break;
            case IslandTheme.Aurora:
                bg = Gradient(("#FF07181C", 0), ("#FF0E1326", 0.55), ("#FF1C1030", 1)); bubble = Solid("#FF1C1030"); border = Solid("#00000000"); break;
            case IslandTheme.Glas:
                bg = Gradient(("#E62C2C2E", 0), ("#F00E0E10", 1)); bubble = Solid("#FF0E0E10"); border = Solid("#33FFFFFF"); break;
            case IslandTheme.Tiefe:
                // "C": black with a little depth at the top – in pixels, so it looks the same at every size
                bg = bubble = PixelFade(34, ("#FF161618", 0), ("#FF000000", 1)); border = TopLight(); break;
            default:
                // "A" Schwarz – like the real Dynamic Island, only a fine light edge on top
                bg = bubble = Solid("#FF000000"); border = TopLight(); break;
        }
        r["IslandBackground"] = bg;
        r["IslandBubbleBackground"] = bubble;
        r["IslandBorder"] = border;
    }

    /// <summary>The 0.5 px rim is lit only along the top edge (fades out within the first 4 px).</summary>
    private static Brush TopLight() => PixelFade(4, ("#26FFFFFF", 0), ("#00FFFFFF", 1));

    /// <summary>Vertical fade over a fixed number of pixels (independent of the island's current size).</summary>
    private static Brush PixelFade(double px, params (string Hex, double Offset)[] stops)
    {
        var g = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, px) };
        foreach (var (hex, off) in stops) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hex), off));
        return Frozen(g);
    }

    public static Color Parse(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }

    private static Brush Solid(string hex) => Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

    private static Brush Gradient(params (string Hex, double Offset)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.35, 1) };
        foreach (var (hex, off) in stops) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hex), off));
        return Frozen(g);
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}

public static class IconFactory
{
    public static SD.Icon CreateTrayIcon()
    {
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(SD.Color.Transparent);
            using var path = new SD.Drawing2D.GraphicsPath();
            var r = new SD.RectangleF(1, 9, 30, 14);
            float d = 14;
            path.AddArc(r.Left, r.Top, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
            path.CloseFigure();
            using var fill = new SD.SolidBrush(SD.Color.FromArgb(255, 245, 245, 247));
            g.FillPath(fill, path);
            using var dot = new SD.SolidBrush(SD.Color.FromArgb(255, 10, 132, 255));
            g.FillEllipse(dot, 20, 12.5f, 7, 7);
            using var bar = new SD.SolidBrush(SD.Color.FromArgb(255, 30, 30, 34));
            g.FillRectangle(bar, 7, 14.5f, 9, 3);
        }
        return SD.Icon.FromHandle(bmp.GetHicon());
    }

    public static ImageSource CreateWindowIcon()
    {
        using var icon = CreateTrayIcon();
        var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }
}
