using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>One notification as the island lists it (from another app, or PeakPill's own price alarms).</summary>
public sealed class IslandNotification : ObservableObject
{
    public long Id { get; init; }
    public string AppId { get; init; }
    public string AppName { get; init; }
    public string Title { get; init; }
    public string Text { get; init; }
    public DateTime Time { get; init; }
    public ImageSource Icon { get; init; }
    /// <summary>Protocol link from the toast (opens the chat etc.), else the app is started.</summary>
    public string Launch { get; init; }
    public Brush Accent { get; init; }
    public string IconKind { get; init; }
    /// <summary>The toast is an incoming call (WhatsApp, Telegram, Teams …).</summary>
    public bool IsCall { get; init; }
    public bool HasImage => Icon != null;

    public string Header => string.IsNullOrEmpty(Title) || Title == AppName ? AppName : L.T($"{AppName} · {Title}");
    public string Ago
    {
        get
        {
            var d = DateTime.Now - Time;
            if (d.TotalMinutes < 1) return "jetzt";
            if (d.TotalMinutes < 60) return L.T($"{(int)d.TotalMinutes} Min");
            if (d.TotalHours < 24) return L.T($"{(int)d.TotalHours} Std");
            return Time.ToString("dd.MM.");
        }
    }
    public void RefreshAgo() => Raise(nameof(Ago));

    private bool _unread;
    /// <summary>Not seen in the Mitteilungen tab yet (blue dot, bold name) – only inside PeakPill.</summary>
    public bool Unread { get => _unread; set => Set(ref _unread, value); }
}

/// <summary>
/// Mirrors Windows' notifications: reads the user's own notification database (the list behind the notification
/// center) every two seconds. Replying from the island is not possible – a click opens the app.
/// </summary>
public sealed class NotificationService : ObservableObject
{
    private static readonly string Db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\Notifications\wpndatabase.db");
    private const int Keep = 60;

    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, (string Name, ImageSource Icon)> _apps = new();
    private HashSet<long> _dismissed = new();
    private long _lastId = -1;
    private bool _busy;

    public ObservableCollection<IslandNotification> Items { get; } = new();
    public int Count => Items.Count;
    /// <summary>A new notification arrived (not on start-up).</summary>
    public event Action<IslandNotification> Arrived;
    /// <summary>A Windows notification from Claude (e.g. "Claude ist fertig") – shown as a way to the Agent page, not as a notification.</summary>
    public event Action<string> AgentNotice;

    private static bool IsAgentApp(string aumid, string name) =>
        (aumid ?? "").Contains("claude", StringComparison.OrdinalIgnoreCase)
        || string.Equals((name ?? "").Trim(), "Claude", StringComparison.OrdinalIgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex CodeHint = new(
        @"code|bestätig|verifi|verify|\botp\b|\bpin\b|\btan\b|anmelde|login|sign.?in|sicherheit|security|einmal|one[- ]time|2fa|passcode",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    // 4–8 digits (or "123 456" / "123-456"), not part of a longer number, a time, a price or a date
    private static readonly System.Text.RegularExpressions.Regex CodeNumber = new(
        @"(?<![\d:.,/€$]|\d-)(\d{3}[ -]\d{3}|\d{4,8})(?![\d:.,/€$%-]|\s?(?:€|eur|uhr|min|gb|mb|kb|%))",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A verification code in a notification ("Dein Google-Bestätigungscode lautet 481923"), else null. Only when
    /// the text talks about a code / sign-in; a 6-digit number wins over others.
    /// </summary>
    public static string FindCode(string title, string text)
    {
        var s = $"{title} {text}";
        if (!CodeHint.IsMatch(s)) return null;
        var all = CodeNumber.Matches(s).Select(m => m.Value.Replace(" ", "").Replace("-", ""))
            .Where(c => !(c.Length == 4 && int.TryParse(c, out var y) && y is >= 1990 and <= 2099)).ToList();
        return all.FirstOrDefault(c => c.Length == 6) ?? all.FirstOrDefault();
    }

    private HashSet<long> _unreadIds = new();
    public int UnreadCount => Items.Count(i => i.Unread);

    /// <summary>Seen in the tab: everything counts as read.</summary>
    public void MarkAllRead()
    {
        bool any = false;
        foreach (var i in Items) if (i.Unread) { i.Unread = false; any = true; }
        if (!any) return;
        Raise(nameof(UnreadCount));
        SaveUnread();
    }

    private void SaveUnread() => Storage.Save("notifications_unread.json", Items.Where(i => i.Unread && i.Id > 0).Select(i => i.Id).ToHashSet());

    public void Start()
    {
        _dismissed = Storage.Load<HashSet<long>>("notifications_dismissed.json") ?? new();
        _unreadIds = Storage.Load<HashSet<long>>("notifications_unread.json") ?? new();
        _t.Tick += async (_, _) => await Poll();
        _t.Start();
        _ = Poll();
    }

    private async Task Poll()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            long last = _lastId;
            var rows = await Task.Run(() => Read(last));
            bool first = _lastId < 0;
            foreach (var r in rows)
            {
                _lastId = Math.Max(_lastId, r.Id);
                if (_dismissed.Contains(r.Id) || Items.Any(i => i.Id == r.Id)) continue;
                var (name, icon) = AppInfo(r.AppId);
                // Claude's own notices ("fertig", "wartet auf dich") are not kept as notifications – the island leads to the Agent page instead
                if (IsAgentApp(r.AppId, name))
                {
                    if (!first) AgentNotice?.Invoke(r.Title);
                    continue;
                }
                var n = new IslandNotification
                {
                    Id = r.Id, AppId = r.AppId, AppName = name, Title = r.Title, Text = r.Text, Time = r.Time,
                    Icon = r.Image ?? icon, Launch = r.Launch, IsCall = r.IsCall,
                    // new ones are unread; on start-up those that were still unread last time
                    Unread = !first || _unreadIds.Contains(r.Id)
                };
                Items.Insert(0, n);
                if (!first) Arrived?.Invoke(n);
            }
            if (_lastId < 0) _lastId = 0;
            while (Items.Count > Keep) Items.RemoveAt(Items.Count - 1);
            if (rows.Count > 0) { Raise(nameof(Count)); Raise(nameof(UnreadCount)); SaveUnread(); }
            foreach (var i in Items) i.RefreshAgo();
        }
        catch (Exception ex) { App.Log(ex); }
        finally { _busy = false; }
    }

    private sealed record Row(long Id, string AppId, string Title, string Text, DateTime Time, string Launch, ImageSource Image, bool IsCall);

    private static List<Row> Read(long afterId)
    {
        var list = new List<Row>();
        if (!File.Exists(Db)) return list;
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private }.ToString());
        con.Open();
        using var cmd = con.CreateCommand();
        // first run: the latest few as history; afterwards only what is new
        cmd.CommandText = afterId < 0
            ? "SELECT n.Id, h.PrimaryId, n.Payload, n.ArrivalTime FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId WHERE n.Type = 'toast' ORDER BY n.Id DESC LIMIT 30"
            : "SELECT n.Id, h.PrimaryId, n.Payload, n.ArrivalTime FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId WHERE n.Type = 'toast' AND n.Id > $id ORDER BY n.Id";
        cmd.Parameters.AddWithValue("$id", afterId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            try
            {
                long id = rd.GetInt64(0);
                string app = rd.IsDBNull(1) ? "" : rd.GetString(1);
                var payload = rd.IsDBNull(2) ? "" : rd.GetValue(2) switch { byte[] b => System.Text.Encoding.UTF8.GetString(b), string s => s, _ => "" };
                long ft = rd.IsDBNull(3) ? 0 : rd.GetInt64(3);
                var (title, text, launch, image, isCall) = Parse(payload);
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(text)) continue;
                list.Add(new Row(id, app, title, text, ft > 0 ? DateTime.FromFileTime(ft) : DateTime.Now, launch, image, isCall));
            }
            catch { }
        }
        if (afterId < 0) list.Reverse(); // oldest first, so the newest ends up on top
        return list;
    }

    /// <summary>Title = first text line, body = the rest; the protocol launch link and an app-logo image if local.</summary>
    private static (string, string, string, ImageSource, bool) Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return ("", "", null, null, false);
        var x = XDocument.Parse(xml.TrimEnd('\0'));
        var texts = x.Descendants("text").Select(t => t.Value.Trim()).Where(t => t.Length > 0).ToList();
        var toast = x.Root;
        string launch = null;
        if (toast != null && (string)toast.Attribute("activationType") == "protocol") launch = (string)toast.Attribute("launch");
        ImageSource img = null;
        var src = x.Descendants("image").Select(i => (string)i.Attribute("src")).FirstOrDefault(s => !string.IsNullOrEmpty(s));
        if (src != null)
        {
            var path = src.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(src[8..]).Replace('/', '\\') : src;
            if (File.Exists(path))
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(path);
                    bi.DecodePixelWidth = 64;
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    img = bi;
                }
                catch { }
            }
        }
        bool isCall = (string)toast?.Attribute("scenario") == "incomingCall";
        return (texts.FirstOrDefault() ?? "", string.Join(" · ", texts.Skip(1)), launch, img, isCall);
    }

    // ---------- app name + icon from the Start menu entry (AUMID) ----------

    private (string, ImageSource) AppInfo(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return ("Windows", null);
        if (_apps.TryGetValue(aumid, out var hit)) return hit;
        string name = null;
        ImageSource icon = null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, ref iid, out var item);
            item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out name);
            if (item is IShellItemImageFactory f && f.GetImage(new SIZE { cx = 64, cy = 64 }, 0x1 /* BIGGERSIZEOK */, out var hbmp) == 0)
            {
                icon = FromHBitmap(hbmp);
                DeleteObject(hbmp);
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(name)) name = MediaService.FriendlyApp(aumid);
        if (string.IsNullOrWhiteSpace(name)) name = aumid;
        if (name.StartsWith("Windows.SystemToast.", StringComparison.OrdinalIgnoreCase)) name = "Windows";
        return _apps[aumid] = (name, icon);
    }

    /// <summary>HBITMAP → BitmapSource keeping the alpha channel (top-down 32-bit copy).</summary>
    private static BitmapSource FromHBitmap(IntPtr hbmp)
    {
        GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out BITMAP bm);
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var px = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try { GetDIBits(dc, hbmp, 0, (uint)h, px, ref bi, 0); }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        src.Freeze();
        return src;
    }

    // ---------- actions ----------

    public void Open(IslandNotification n)
    {
        try
        {
            if (!string.IsNullOrEmpty(n.Launch) && Uri.TryCreate(n.Launch, UriKind.Absolute, out var u) && u.Scheme is not ("file" or "javascript"))
                Process.Start(new ProcessStartInfo(n.Launch) { UseShellExecute = true });
            else if (!string.IsNullOrEmpty(n.AppId) && !n.AppId.StartsWith("PeakPill", StringComparison.Ordinal))
                Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + n.AppId) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public void Dismiss(IslandNotification n)
    {
        Items.Remove(n);
        if (n.Id > 0) { _dismissed.Add(n.Id); SaveDismissed(); }
        Raise(nameof(Count));
        Raise(nameof(UnreadCount));
    }

    public void ClearAll()
    {
        foreach (var n in Items.Where(n => n.Id > 0)) _dismissed.Add(n.Id);
        Items.Clear();
        SaveDismissed();
        SaveUnread();
        Raise(nameof(Count));
        Raise(nameof(UnreadCount));
    }

    /// <summary>PeakPill's own entries (price alarms) – they live only in this list.</summary>
    private long _ownId = -1;
    public IslandNotification AddOwn(string title, string text, string iconKind, Brush accent, string launch = null)
    {
        var n = new IslandNotification { Id = _ownId--, AppId = "PeakPill", AppName = L.T("Preisalarm"), Title = title, Text = text, Time = DateTime.Now, IconKind = iconKind, Accent = accent, Launch = launch, Unread = true };
        Items.Insert(0, n);
        Raise(nameof(Count));
        Raise(nameof(UnreadCount));
        return n;
    }

    private void SaveDismissed()
    {
        if (_dismissed.Count > 800) _dismissed = _dismissed.OrderByDescending(x => x).Take(500).ToHashSet();
        Storage.Save("notifications_dismissed.json", _dismissed);
    }

    // ---------- native ----------

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
}
