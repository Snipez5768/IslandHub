using System.Windows.Input;
using System.Windows.Interop;

namespace IslandHub.Core;

/// <summary>Hidden message-only window: receives WM_HOTKEY and WM_CLIPBOARDUPDATE.</summary>
public sealed class MessageWindow : IDisposable
{
    public HwndSource Source { get; }
    public IntPtr Handle => Source.Handle;
    public event Action<int, IntPtr, IntPtr> Received;

    public MessageWindow()
    {
        var p = new HwndSourceParameters("IslandHubMessages")
        {
            Width = 0, Height = 0, WindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        };
        Source = new HwndSource(p);
        Source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY || msg == Native.WM_CLIPBOARDUPDATE)
            Received?.Invoke(msg, w, l);
        return IntPtr.Zero;
    }

    public void Dispose() => Source.Dispose();
}

public sealed record HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public static bool TryParse(string s, out HotkeyGesture g)
    {
        g = null;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var mods = ModifierKeys.None;
        var key = Key.None;
        foreach (var raw in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "strg": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": case "umschalt": mods |= ModifierKeys.Shift; break;
                case "win": case "windows": mods |= ModifierKeys.Windows; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (!Enum.TryParse(raw, true, out key)) return false;
                    break;
            }
        }
        if (key == Key.None) return false;
        g = new HotkeyGesture(mods, key);
        return true;
    }

    public string ToStorage()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    public string ToDisplay()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Strg");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join(" + ", parts);
    }

    public static string Display(string stored) =>
        TryParse(stored, out var g) ? g.ToDisplay() : "";

    public static string KeyName(Key k) => k switch
    {
        Key.Space => "Leertaste",
        Key.Return => "Enter",
        Key.Back => "Rücktaste",
        Key.Snapshot => "Druck",
        Key.Prior => "Bild↑",
        Key.Next => "Bild↓",
        Key.Insert => "Einfg",
        Key.Delete => "Entf",
        Key.Home => "Pos1",
        Key.End => "Ende",
        Key.Up => "↑", Key.Down => "↓", Key.Left => "←", Key.Right => "→",
        Key.OemPlus => "+", Key.OemMinus => "-", Key.OemComma => ",", Key.OemPeriod => ".",
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(k - Key.NumPad0),
        _ => k.ToString()
    };

    /// <summary>Ctrl+Alt behaves like AltGr on German layouts; these keys produce characters there.</summary>
    public bool CollidesWithAltGr()
    {
        if (Modifiers != (ModifierKeys.Control | ModifierKeys.Alt)) return false;
        return Key is Key.Q or Key.E or Key.M or Key.D2 or Key.D3 or Key.D7 or Key.D8 or Key.D9 or Key.D0
            or Key.OemOpenBrackets or Key.OemPlus or Key.OemBackslash or Key.Oem102;
    }
}

public sealed class HotkeyService
{
    private readonly MessageWindow _win;
    private readonly Dictionary<int, HotkeyAction> _map = new();
    private int _nextId = 1;

    public event Action<HotkeyAction> Pressed;

    public HotkeyService(MessageWindow win)
    {
        _win = win;
        _win.Received += (msg, w, _) =>
        {
            if (msg == Native.WM_HOTKEY && _map.TryGetValue(w.ToInt32(), out var a))
                Pressed?.Invoke(a);
        };
    }

    public void UnregisterAll()
    {
        foreach (var id in _map.Keys) Native.UnregisterHotKey(_win.Handle, id);
        _map.Clear();
    }

    /// <summary>Registers every configured shortcut and returns the ones Windows refused.</summary>
    public List<HotkeyAction> ApplyFromSettings(AppSettings s)
    {
        UnregisterAll();
        var failed = new List<HotkeyAction>();
        foreach (var info in HotkeyInfo.All)
        {
            if (!s.Hotkeys.TryGetValue(info.Action.ToString(), out var str) || string.IsNullOrWhiteSpace(str)) continue;
            if (!HotkeyGesture.TryParse(str, out var g)) { failed.Add(info.Action); continue; }
            int id = _nextId++;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(g.Key);
            if (Native.RegisterHotKey(_win.Handle, id, (uint)g.Modifiers | Native.MOD_NOREPEAT, vk))
                _map[id] = info.Action;
            else
                failed.Add(info.Action);
        }
        return failed;
    }
}
