using System.Diagnostics;
using System.Runtime.InteropServices;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>
/// Answering a message from the island (user wish 2026-11-16). Windows does not let other programs use a
/// notification's own reply field, so PeakPill does what you would do: bring the messenger to the front, open the
/// chat through the app's own search (the sender's name), put the text in – and, if "Antwort gleich senden" is on,
/// press Enter. Every step first checks that the messenger is still in front; otherwise it stops (no key ever lands
/// in another program). Off (default): the text waits in the chat and you send it yourself.
/// </summary>
public static class ReplyService
{
    private sealed record Messenger(string Match, string[] Processes, ushort SearchKey, bool Ctrl = true);

    private const ushort VK_F = 0x46, VK_K = 0x4B, VK_E = 0x45, VK_V = 0x56, VK_RETURN = 0x0D, VK_CONTROL = 0x11, VK_ESCAPE = 0x1B;

    private static readonly Messenger[] Known =
    {
        new("WhatsApp", new[] { "WhatsApp", "WhatsApp.Root" }, VK_F),
        new("Telegram", new[] { "Telegram" }, VK_F),
        new("Signal", new[] { "Signal" }, VK_F),
        new("Discord", new[] { "Discord" }, VK_K),
        new("Slack", new[] { "slack" }, VK_K),
        new("Teams", new[] { "ms-teams", "Teams" }, VK_E),
        new("Messenger", new[] { "Messenger" }, VK_F),
    };

    public static async Task<bool> SendAsync(IslandNotification n, string text, bool autoSend)
    {
        var m = Known.FirstOrDefault(k => (n.AppName ?? "").Contains(k.Match, StringComparison.OrdinalIgnoreCase));
        if (m == null || string.IsNullOrWhiteSpace(n.Title)) return false;
        try
        {
            // the text goes in by paste (any language, emoji); PeakPill does not keep it in its history
            App.Hub.Clipboard.SetClipboard(new ClipItem { Kind = ClipKind.Text, Text = text });

            var win = FindWindow(m);
            if (win == IntPtr.Zero)
            {
                App.Hub.Notifications.Open(n); // the notification's own link may open the chat directly
                for (int i = 0; i < 30 && (win = FindWindow(m)) == IntPtr.Zero; i++) await Task.Delay(100);
                if (win == IntPtr.Zero) return false;
                await Task.Delay(1200);
            }
            PrivacyService.Focus(win);
            await Task.Delay(350);
            if (!Front(win)) return false;

            // open the chat through the app's search: shortcut, the name, Enter on the first hit
            Chord(VK_CONTROL, m.SearchKey);
            await Task.Delay(300);
            if (!Front(win)) return false;
            Type(n.Title.Trim());
            await Task.Delay(800);
            if (!Front(win)) return false;
            Key(VK_RETURN);
            await Task.Delay(700);
            if (!Front(win)) return false;

            Chord(VK_CONTROL, VK_V);
            await Task.Delay(200);
            if (autoSend && Front(win)) Key(VK_RETURN);
            return true;
        }
        catch (Exception ex) { App.Log(ex); return false; }
    }

    private static IntPtr FindWindow(Messenger m)
    {
        foreach (var name in m.Processes)
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
        return IntPtr.Zero;
    }

    /// <summary>The messenger (or one of its own windows) is in front.</summary>
    private static bool Front(IntPtr win)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == win) return true;
        Native.GetWindowThreadProcessId(fg, out var a);
        Native.GetWindowThreadProcessId(win, out var b);
        return a == b;
    }

    // ---------- keys (SendInput) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public KEYBDINPUT ki; public long pad1; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    private const uint KEYUP = 0x0002, UNICODE = 0x0004;

    private static INPUT In(ushort vk, ushort scan, uint flags) => new() { type = 1, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } };

    private static void Send(params INPUT[] i) => SendInput((uint)i.Length, i, Marshal.SizeOf<INPUT>());

    private static void Key(ushort vk) => Send(In(vk, 0, 0), In(vk, 0, KEYUP));

    private static void Chord(ushort mod, ushort vk) => Send(In(mod, 0, 0), In(vk, 0, 0), In(vk, 0, KEYUP), In(mod, 0, KEYUP));

    private static void Type(string s)
    {
        var list = new List<INPUT>();
        foreach (var ch in s) { list.Add(In(0, ch, UNICODE)); list.Add(In(0, ch, UNICODE | KEYUP)); }
        Send(list.ToArray());
    }
}
