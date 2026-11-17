using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PeakPill.Core;

namespace PeakPill.Services;

public enum ClipKind { Text, Link, Image, Files }

public sealed class ClipItem : ObservableObject
{
    private DateTime _time = DateTime.Now;
    private bool _pinned;
    private int _index;
    private ImageSource _thumb;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string Text { get; set; }
    public List<string> Files { get; set; }
    public string ImageFile { get; set; }
    public string Hash { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public string Source { get; set; }
    public bool IsScreenshot { get; set; }

    public DateTime Time { get => _time; set { Set(ref _time, value); Raise(nameof(Meta)); } }
    public bool Pinned { get => _pinned; set { if (Set(ref _pinned, value)) Raise(nameof(DayGroup)); } }

    [JsonIgnore] public int Index { get => _index; set { if (Set(ref _index, value)) Raise(nameof(IndexText)); } }
    [JsonIgnore] public string IndexText => _index is >= 1 and <= 9 ? _index.ToString() : "";
    [JsonIgnore] public bool IsImage => Kind == ClipKind.Image;
    [JsonIgnore]
    public string IconName => Kind switch
    {
        ClipKind.Link => "link",
        ClipKind.Image => "photo",
        ClipKind.Files => "folder",
        _ => "doc"
    };

    [JsonIgnore]
    public string Preview => Kind switch
    {
        ClipKind.Image => IsScreenshot ? "Screenshot" : L.T("Bild"),
        ClipKind.Files => Files == null ? "" :
            (Files.Count == 1 ? Path.GetFileName(Files[0]) : L.T($"{Files.Count} Dateien: ") + string.Join(", ", Files.Take(4).Select(Path.GetFileName))),
        _ => Shorten(Text)
    };

    [JsonIgnore]
    public string Meta
    {
        get
        {
            var parts = new List<string> { Relative(Time) };
            if (Kind == ClipKind.Image && ImageWidth > 0) parts.Add($"{ImageWidth}×{ImageHeight}");
            if (Kind is ClipKind.Text && Text != null) parts.Add(L.T($"{Text.Length} Zeichen"));
            if (!string.IsNullOrEmpty(Source)) parts.Add(Source);
            return string.Join(" · ", parts);
        }
    }

    [JsonIgnore]
    public ImageSource Thumb
    {
        get
        {
            if (_thumb != null || Kind != ClipKind.Image || ImageFile == null || !File.Exists(ImageFile)) return _thumb;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(ImageFile);
                bi.DecodePixelWidth = 160;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                _thumb = bi;
            }
            catch { }
            return _thumb;
        }
    }

    public void RefreshMeta() { Raise(nameof(Meta)); Raise(nameof(Ago)); Raise(nameof(DayGroup)); }

    // ---------- the clipboard layouts (approved mockup 2026-11-14): one title line, one quiet line, a type chip ----------

    private static readonly System.Text.RegularExpressions.Regex CodeLike =
        new(@"^[\w\-./:#=+]{16,}$|[{};]\s*$|^\s*(var|let|const|public|private|function|def|import|using|class)\s", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>Hashes, tokens, paths, code: shown as "Code" (mono, purple chip).</summary>
    [JsonIgnore] public bool IsCode => Kind == ClipKind.Text && Text is { Length: > 0 } t && !t.Trim().Contains(' ') && CodeLike.IsMatch(t.Trim())
                                       || Kind == ClipKind.Text && Text is { Length: > 0 } c && c.Contains('\n') && CodeLike.IsMatch(c);

    [JsonIgnore]
    public string Title => Kind switch
    {
        ClipKind.Link => ShortUrl(Text),
        _ => Preview
    };

    [JsonIgnore]
    public string Sub
    {
        get
        {
            var parts = new List<string>();
            switch (Kind)
            {
                case ClipKind.Link: parts.Add(Domain(Text)); break;
                case ClipKind.Image when ImageWidth > 0: parts.Add($"{ImageWidth} × {ImageHeight}"); break;
                case ClipKind.Files: parts.Add(Files is { Count: > 1 } ? L.T($"{Files.Count} Dateien") : L.T("Datei")); break;
                case ClipKind.Text when IsCode: parts.Add("Code"); break;
            }
            if (!string.IsNullOrEmpty(Source)) parts.Add(Source);
            return parts.Count == 0 ? "Text" : string.Join(" · ", parts);
        }
    }

    /// <summary>Short age for the right edge: "jetzt", "5 Min", "3 Std", "gestern", "12.11.".</summary>
    [JsonIgnore]
    public string Ago
    {
        get
        {
            var d = DateTime.Now - Time;
            if (d.TotalSeconds < 45) return "jetzt";
            if (d.TotalMinutes < 60) return L.T($"{(int)d.TotalMinutes} Min");
            if (Time.Date == DateTime.Today) return L.T($"{(int)d.TotalHours} Std");
            if (Time.Date == DateTime.Today.AddDays(-1)) return "gestern";
            return Time.ToString("dd.MM.");
        }
    }

    /// <summary>Section in the list: Angeheftet · Heute · Gestern · Diese Woche · Älter.</summary>
    [JsonIgnore]
    public string DayGroup =>
        Pinned ? L.T("Angeheftet")
        : Time.Date == DateTime.Today ? L.T("Heute")
        : Time.Date == DateTime.Today.AddDays(-1) ? L.T("Gestern")
        : Time.Date > DateTime.Today.AddDays(-7) ? L.T("Diese Woche")
        : L.T("Älter");

    private static readonly Brush TextTint = Frozen(0xAE, 0xAE, 0xB2), LinkTint = Frozen(0x64, 0xD2, 0xFF),
                                  CodeTint = Frozen(0xBF, 0x5A, 0xF2), FileTint = Frozen(0xFF, 0x9F, 0x0A);
    private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    private static Brush Ground(Brush b, byte a) { var c = ((SolidColorBrush)b).Color; var x = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); x.Freeze(); return x; }

    [JsonIgnore] public string TypeGlyph => Kind switch { ClipKind.Link => "link", ClipKind.Files => "folder", _ => IsCode ? "code" : "doc" };
    [JsonIgnore] public Brush TypeBrush => Kind switch { ClipKind.Link => LinkTint, ClipKind.Files => FileTint, _ => IsCode ? CodeTint : TextTint };
    [JsonIgnore] public Brush TypeGround => Kind == ClipKind.Text && !IsCode ? Ground(TextTint, 0x1A) : Ground(TypeBrush, 0x26);

    private static string ShortUrl(string u)
    {
        if (string.IsNullOrEmpty(u)) return "";
        u = u.Trim();
        foreach (var p in new[] { "https://", "http://" }) if (u.StartsWith(p, StringComparison.OrdinalIgnoreCase)) u = u[p.Length..];
        if (u.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) u = u[4..];
        return u.TrimEnd('/');
    }

    private static string Domain(string u)
    {
        try { var h = new Uri(u.Trim()).Host; return h.StartsWith("www.") ? h[4..] : h; }
        catch { return "Link"; }
    }

    private bool _expanded, _justCopied;

    /// <summary>Briefly true after the entry was copied – the row shows "Kopiert ✓".</summary>
    [JsonIgnore] public bool JustCopied { get => _justCopied; set => Set(ref _justCopied, value); }
    private ImageSource _large;

    /// <summary>Row is opened up in the clipboard list (full text / big picture).</summary>
    [JsonIgnore] public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }

    [JsonIgnore]
    public string FullText => Kind switch
    {
        ClipKind.Files => string.Join(Environment.NewLine, Files ?? new()),
        ClipKind.Image => "",
        _ => Text ?? ""
    };

    [JsonIgnore]
    public ImageSource LargeImage
    {
        get
        {
            if (_large != null || Kind != ClipKind.Image || ImageFile == null || !File.Exists(ImageFile)) return _large;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(ImageFile);
                bi.DecodePixelWidth = Math.Min(720, Math.Max(1, ImageWidth));
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bi.EndInit();
                bi.Freeze();
                _large = bi;
            }
            catch { }
            return _large;
        }
    }

    public void ResetThumb()
    {
        _thumb = null;
        _large = null;
        Raise(nameof(Thumb));
        Raise(nameof(LargeImage));
        Raise(nameof(Meta));
    }

    public bool Matches(string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return true;
        var hay = (Kind == ClipKind.Files ? string.Join(" ", Files ?? new()) : Text ?? Preview) + " " + Source;
        return hay.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string Shorten(string t)
    {
        if (string.IsNullOrEmpty(t)) return "";
        t = t.Trim().Replace("\r\n", " ").Replace('\n', ' ').Replace('\t', ' ');
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return t.Length > 240 ? t[..240] + "…" : t;
    }

    public static string Relative(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 45) return L.T("gerade eben");
        if (d.TotalMinutes < 60) return L.T($"vor {(int)d.TotalMinutes} Min");
        if (d.TotalHours < 24 && t.Date == DateTime.Today) return L.T($"heute {t:HH:mm}");
        if (t.Date == DateTime.Today.AddDays(-1)) return L.T($"gestern {t:HH:mm}");
        return t.ToString("dd.MM. HH:mm");
    }
}

/// <summary>
/// Watches the normal Windows clipboard (Ctrl+C), keeps a history and implements the optional
/// queue ("Stapel") mode: every Ctrl+V pastes the next copied item in order.
/// </summary>
public sealed class ClipboardService : ObservableObject
{
    public static readonly string ClipDir = Path.Combine(Storage.Dir, "clips");

    public ObservableCollection<ClipItem> Items { get; } = new();
    public ObservableCollection<ClipItem> Queue { get; } = new();

    public event Action<string> Notice;
    /// <summary>Raised for every new history entry; the bool is true when the user copied it (Ctrl+C), false for PeakPill's own adds.</summary>
    public event Action<ClipItem, bool> Captured;

    private DateTime _suppressUntil;
    private readonly KeyboardHook _hook = new();
    private readonly Debouncer _save = new(1200);
    private ForegroundTracker _fg;
    private bool _queueMode;
    private DateTime _lastQueued;

    public bool QueueMode
    {
        get => _queueMode;
        set
        {
            if (!Set(ref _queueMode, value)) return;
            Queue.Clear();
            UpdateHook();
            App.Settings.QueueMode = value;
            App.SaveSettings();
            Raise(nameof(QueueCount));
        }
    }

    public int QueueCount => Queue.Count;

    public void Start(MessageWindow win, ForegroundTracker fg)
    {
        _fg = fg;
        Directory.CreateDirectory(ClipDir);
        Load();
        Queue.CollectionChanged += (_, _) => Raise(nameof(QueueCount));
        Items.CollectionChanged += OnItemsChanged;
        _hook.PasteDetected += () => Ui.D.BeginInvoke(new Action(OnPasteDetected));
        _owner = win.Handle;
        win.Received += (msg, w, _) =>
        {
            if (msg == Native.WM_CLIPBOARDUPDATE) OnClipboardUpdate();
            else if (msg == WM_RENDERFORMAT) OnRenderFormat((uint)w.ToInt64());
            else if (msg == WM_RENDERALLFORMATS) OnRenderAll();
            else if (msg == WM_DESTROYCLIPBOARD) _promised = null; // someone else owns the clipboard now
        };
        Native.AddClipboardFormatListener(win.Handle);
        _queueMode = App.Settings.QueueMode;
        UpdateHook();
    }

    private void OnItemsChanged(object s, NotifyCollectionChangedEventArgs e) => _save.Run(Save);

    /// <summary>Ctrl+V is watched for the queue and for "open what just happened" (just pasted → clipboard page).</summary>
    private void UpdateHook()
    {
        if (_queueMode || App.Settings.SmartTab) _hook.Install(); else _hook.Uninstall();
    }

    public void RefreshHook() => UpdateHook();

    /// <summary>The user pressed Ctrl+V somewhere (needs the hook – see <see cref="UpdateHook"/>).</summary>
    public event Action Pasted;

    // ---------- capture ----------

    private async void OnClipboardUpdate()
    {
        if (DateTime.UtcNow < _suppressUntil) return;
        await Task.Delay(70);
        if (DateTime.UtcNow < _suppressUntil) return;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var item = ReadClipboard();
                if (item != null) AddCaptured(item, true);
                return;
            }
            catch (COMException) { await Task.Delay(60); }
            catch (ExternalException) { await Task.Delay(60); }
            catch (Exception ex) { App.Log(ex); return; }
        }
    }

    private ClipItem ReadClipboard()
    {
        var d = Clipboard.GetDataObject();
        if (d == null) return null;

        // Respect apps (password managers) that ask not to be recorded.
        if (d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return null;
        if (d.GetDataPresent("CanIncludeInClipboardHistory") && d.GetData("CanIncludeInClipboardHistory") is MemoryStream ms0)
        {
            var b = ms0.ToArray();
            if (b.Length >= 4 && BitConverter.ToInt32(b, 0) == 0) return null;
        }

        string source = ForegroundTracker.AppNameOf(Native.GetForegroundWindow());

        if (d.GetDataPresent(DataFormats.FileDrop) && d.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            return new ClipItem { Kind = ClipKind.Files, Files = files.ToList(), Source = source };

        bool hasText = d.GetDataPresent(DataFormats.UnicodeText);
        if (!hasText && (d.GetDataPresent("PNG") || d.GetDataPresent(DataFormats.Bitmap)))
        {
            BitmapSource img = null;
            if (d.GetDataPresent("PNG") && d.GetData("PNG") is MemoryStream png)
            {
                try { img = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); } catch { img = null; }
            }
            if (img == null)
            {
                var raw = Clipboard.GetImage();
                if (raw != null) img = new FormatConvertedBitmap(raw, PixelFormats.Bgr32, null, 0); // DIBs often carry a bogus alpha channel
            }
            if (img != null) return CreateImageItem(img, source, false);
        }

        if (hasText)
        {
            var text = d.GetData(DataFormats.UnicodeText) as string;
            if (string.IsNullOrEmpty(text)) return null;
            bool link = Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && !text.Trim().Contains(' ');
            return new ClipItem { Kind = link ? ClipKind.Link : ClipKind.Text, Text = text, Source = source };
        }
        return null;
    }

    private static ClipItem CreateImageItem(BitmapSource img, string source, bool screenshot)
    {
        var item = new ClipItem { Kind = ClipKind.Image, Source = source, IsScreenshot = screenshot, ImageWidth = img.PixelWidth, ImageHeight = img.PixelHeight };
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var bytes = ms.ToArray();
        item.Hash = Convert.ToHexString(SHA1.HashData(bytes));
        item.ImageFile = Path.Combine(ClipDir, item.Id + ".png");
        File.WriteAllBytes(item.ImageFile, bytes);
        return item;
    }

    private void AddCaptured(ClipItem item, bool byUser)
    {
        var existing = FindDuplicate(item);
        if (existing != null)
        {
            if (item.ImageFile != null && item.ImageFile != existing.ImageFile) TryDelete(item.ImageFile);
            int i = Items.IndexOf(existing);
            if (i > 0) Items.Move(i, 0);
            existing.Time = DateTime.Now;
            item = existing;
        }
        else
        {
            Items.Insert(0, item);
            Trim();
        }

        if (_queueMode)
        {
            // browsers often announce ONE copy two or three times (text, then HTML …) – that must not queue it twice
            bool echo = Queue.Count > 0 && Queue[^1] == item && DateTime.Now - _lastQueued < TimeSpan.FromSeconds(2);
            if (!echo)
            {
                Queue.Add(item);
                _lastQueued = DateTime.Now;
            }
            StackLog($"copied ({item.Kind}, from {item.Source ?? "?"}){(echo ? " – same copy again, not queued twice" : "")} – {Queue.Count} in the stack");
            // the head is offered even right after the first copy – so the very first paste is noticed too
            SetQueueHead();
        }
        Captured?.Invoke(item, byUser);
    }

    private ClipItem FindDuplicate(ClipItem n) => Items.FirstOrDefault(x => x.Kind == n.Kind && n.Kind switch
    {
        ClipKind.Image => x.Hash != null && x.Hash == n.Hash,
        ClipKind.Files => x.Files != null && n.Files != null && x.Files.SequenceEqual(n.Files),
        _ => x.Text == n.Text
    });

    private void Trim()
    {
        int limit = Math.Max(10, App.Settings.ClipboardLimit);
        while (Items.Count(i => !i.Pinned) > limit)
        {
            var victim = Items.Last(i => !i.Pinned);
            Remove(victim);
        }
    }

    // ---------- public API ----------

    public ClipItem AddImage(BitmapSource img, bool screenshot)
    {
        var item = CreateImageItem(img, screenshot ? "Screenshot" : null, screenshot);
        SetClipboard(item);
        AddCaptured(item, false);
        return item;
    }

    public ClipItem AddText(string text, string source)
    {
        var item = new ClipItem { Kind = ClipKind.Text, Text = text, Source = source };
        SetClipboard(item);
        AddCaptured(item, false);
        return item;
    }

    /// <summary>Replaces an image entry's file (after editing) and puts the new image on the clipboard.</summary>
    public void UpdateImage(ClipItem item, BitmapSource img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using (var fs = File.Create(item.ImageFile)) enc.Save(fs);
        item.ImageWidth = img.PixelWidth;
        item.ImageHeight = img.PixelHeight;
        item.ResetThumb();
        SetClipboard(item);
        _save.Run(Save);
    }

    public bool SetClipboard(ClipItem it)
    {
        _suppressUntil = DateTime.UtcNow.AddMilliseconds(700);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                switch (it.Kind)
                {
                    case ClipKind.Text:
                    case ClipKind.Link:
                        Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, it.Text ?? ""), true);
                        break;
                    case ClipKind.Image:
                        if (it.ImageFile == null || !File.Exists(it.ImageFile)) return false;
                        var bytes = File.ReadAllBytes(it.ImageFile);
                        var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        var dobj = new DataObject();
                        dobj.SetImage(frame);
                        dobj.SetData("PNG", new MemoryStream(bytes), false);
                        Clipboard.SetDataObject(dobj, true);
                        break;
                    case ClipKind.Files:
                        var sc = new System.Collections.Specialized.StringCollection();
                        foreach (var f in it.Files ?? new()) if (File.Exists(f) || Directory.Exists(f)) sc.Add(f);
                        if (sc.Count == 0) return false;
                        Clipboard.SetFileDropList(sc);
                        break;
                }
                _suppressUntil = DateTime.UtcNow.AddMilliseconds(500);
                return true;
            }
            catch (COMException) { Thread.Sleep(50); }
            catch (ExternalException) { Thread.Sleep(50); }
            catch (Exception ex) { App.Log(ex); return false; }
        }
        return false;
    }

    private void OnPasteDetected()
    {
        Pasted?.Invoke();
        if (!_queueMode || Queue.Count == 0) return;
        // a promised text tells us itself when it is pasted (any way: Ctrl+V, menu, Shift+Ins, macro keys) – unless a
        // background reader took it first: then Windows will not ask again and this Ctrl+V is the only sign
        if (_promised != null && Queue[0] == _promised && (_promisedTaken || !_promisedRead)) return;
        StackLog("Ctrl+V seen" + (_promisedRead ? " (text was read in the background before)" : ""));
        // images / files: give the target app a moment to read the clipboard before we swap in the next item
        var head = Queue[0];
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        t.Tick += (_, _) => { t.Stop(); Advance(head); };
        t.Start();
    }

    /// <summary>The head of the queue was pasted: next one up, and the island says it once.</summary>
    private void Advance(ClipItem pasted)
    {
        if (!_queueMode || Queue.Count == 0 || Queue[0] != pasted) return;
        Queue.RemoveAt(0);
        StackLog($"pasted – {Queue.Count} left");
        if (Queue.Count > 0)
        {
            SetQueueHead();
            var next = Queue[0].Title ?? "";
            if (next.Length > 24) next = next[..23].TrimEnd() + "…";
            Notice?.Invoke(L.T($"Eingefügt · als Nächstes: {next}"));
        }
        else Notice?.Invoke(L.T("Eingefügt · Stapel leer"));
    }

    // ---------- queue: the next text is only PROMISED to Windows (delayed rendering, 2026-11-16) ----------
    // The keyboard hook missed pastes from the menu, Shift+Ins and keys sent by mouse / macro software. Now Windows
    // itself asks PeakPill for the text the moment an app really pastes it – that moment advances the queue.
    // "ExcludeClipboardContentFromMonitorProcessing" + "CanIncludeInClipboardHistory = 0" keep clipboard managers and
    // Windows' own history (Win+V) from reading it early (which would look like a paste).

    private const int WM_RENDERFORMAT = 0x0305, WM_RENDERALLFORMATS = 0x0306, WM_DESTROYCLIPBOARD = 0x0307;
    private const uint CF_UNICODETEXT = 13;
    private static readonly uint FmtExclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint FmtNoHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint FmtNoCloud = RegisterClipboardFormat("CanUploadToCloudClipboard");
    private IntPtr _owner;
    private ClipItem _promised;
    private bool _promisedTaken, _promisedRead;
    private uint _promisedAt; // last-input tick when the text was put there

    private void SetQueueHead()
    {
        if (Queue.Count == 0) return;
        if (!Promise(Queue[0])) SetClipboard(Queue[0]);
    }

    private bool Promise(ClipItem it)
    {
        if (it.Kind is not (ClipKind.Text or ClipKind.Link) || _owner == IntPtr.Zero || it.Text == null) return false;
        _suppressUntil = DateTime.UtcNow.AddMilliseconds(700);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (OpenClipboard(_owner))
            {
                try
                {
                    EmptyClipboard();
                    _promised = it;
                    _promisedTaken = _promisedRead = false;
                    _promisedAt = LastInputTick(); // only input after this moment makes a read a paste
                    SetClipboardData(CF_UNICODETEXT, IntPtr.Zero); // promised – rendered on request
                    SetClipboardData(FmtExclude, Dword(0));
                    SetClipboardData(FmtNoHistory, Dword(0));
                    SetClipboardData(FmtNoCloud, Dword(0));
                    StackLog($"ready: item {Queue.IndexOf(it) + 1} of {Queue.Count}");
                    return true;
                }
                finally { CloseClipboard(); _suppressUntil = DateTime.UtcNow.AddMilliseconds(500); }
            }
            Thread.Sleep(40);
        }
        StackLog("could not open the clipboard – the text is put there directly (Ctrl+V watch counts)");
        return false;
    }

    /// <summary>Someone reads the promised text: hand it over now. Was it a paste (you did something since it was
    /// put there – a key, a click, a macro), the queue moves on a moment later.
    /// Programs that watch the clipboard in the background (2026-11-16, user: "Stapeleinfügen klappt immer noch nicht")
    /// read it right after it is put there, with nobody touching anything. Counted as a paste, they skipped items;
    /// and once read, the text sits on the clipboard for real – Windows does not ask again, so the real Ctrl+V went
    /// unnoticed and pasted the same text over and over. Such a read does not count; the Ctrl+V watch takes over.</summary>
    private void OnRenderFormat(uint format)
    {
        var it = _promised;
        if (it == null || format != CF_UNICODETEXT) return;
        SetClipboardData(CF_UNICODETEXT, GlobalText(it.Text));
        if (_promisedTaken) return;
        uint lastInput = LastInputTick();
        bool touched = unchecked((int)(lastInput - _promisedAt)) > 0; // input after the text was put there
        var req = GetOpenClipboardWindow();
        Native.GetWindowThreadProcessId(req, out uint rpid);
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint fpid);
        bool front = req == IntPtr.Zero || rpid == fpid; // the app in front reads (or it did not say who it is)
        bool paste = touched && front;
        _promisedTaken = paste;
        _promisedRead = true;
        StackLog($"read by {(req == IntPtr.Zero ? "(no window)" : ProcName(req))}, in front {ProcName(Native.GetForegroundWindow())}, " +
                 $"input {unchecked(Environment.TickCount - (int)lastInput)} ms ago → {(paste ? "paste" : "background read, not counted")}");
        if (!paste) return;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        t.Tick += (_, _) => { t.Stop(); Advance(it); };
        t.Start();
    }

    private static string ProcName(IntPtr hwnd)
    {
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return "?"; }
    }

    private static uint LastInputTick()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref li) ? li.dwTime : 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO li);

    /// <summary>A short local log of the queue (no clipboard content – only what happened and who read), so a
    /// queue that misbehaves on someone's PC can be understood: %AppData%\PeakPill\stack-log.txt, last 300 lines.</summary>
    private static void StackLog(string what)
    {
        try
        {
            var f = Storage.PathOf("stack-log.txt");
            var lines = File.Exists(f) ? File.ReadAllLines(f).ToList() : new List<string>();
            lines.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {what}");
            if (lines.Count > 300) lines.RemoveRange(0, lines.Count - 300);
            File.WriteAllLines(f, lines);
        }
        catch { }
    }

    /// <summary>PeakPill closes: the promised text is written for real, so it stays pasteable.</summary>
    private void OnRenderAll()
    {
        var it = _promised;
        if (it == null || !OpenClipboard(_owner)) return;
        try { if (GetClipboardOwner() == _owner) SetClipboardData(CF_UNICODETEXT, GlobalText(it.Text)); }
        finally { CloseClipboard(); }
    }

    private static IntPtr GlobalText(string s)
    {
        var bytes = Encoding.Unicode.GetBytes((s ?? "") + "\0");
        var h = GlobalAlloc(0x0002, (UIntPtr)bytes.Length); // GMEM_MOVEABLE
        var p = GlobalLock(h);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        GlobalUnlock(h);
        return h;
    }

    private static IntPtr Dword(int v)
    {
        var h = GlobalAlloc(0x0002, (UIntPtr)4);
        var p = GlobalLock(h);
        Marshal.WriteInt32(p, v);
        GlobalUnlock(h);
        return h;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);

    /// <summary>Queue test only: lets the test's own (injected) Ctrl+V count as a paste.</summary>
    public void AcceptInjectedPasteForTest() => _hook.AcceptInjected = true;
    /// <summary>Queue test only: what happens right after the user pressed Ctrl+V.</summary>
    public void SimulatePasteForTest() => OnPasteDetected();
    public bool HookRunningForTest => _hook.Installed;

    public void RemoveFromQueue(ClipItem item)
    {
        bool wasHead = Queue.Count > 0 && Queue[0] == item;
        Queue.Remove(item);
        if (wasHead && Queue.Count > 0) SetQueueHead();
    }

    public void ClearQueue() => Queue.Clear();

    /// <summary>What was copied within <paramref name="within"/> (newest first in the history), oldest first.</summary>
    public List<ClipItem> RecentCopies(TimeSpan within, int max = 9) =>
        Items.Where(i => DateTime.Now - i.Time < within).Take(max).Reverse().ToList();

    /// <summary>Copies from before the stack was on go in first – they were copied before anything that is in it.</summary>
    public void AddToQueueFront(IList<ClipItem> items)
    {
        if (!_queueMode) return;
        int at = 0;
        foreach (var it in items)
            if (!Queue.Contains(it)) Queue.Insert(at++, it);
        if (at == 0) return;
        StackLog($"{at} earlier copies added – {Queue.Count} in the stack");
        SetQueueHead();
        var next = Queue[0].Title ?? "";
        if (next.Length > 24) next = next[..23].TrimEnd() + "…";
        Notice?.Invoke(L.T($"{Queue.Count} im Stapel · als Erstes: {next}"));
    }

    public void Remove(ClipItem item)
    {
        Items.Remove(item);
        Queue.Remove(item);
        if (item.ImageFile != null && !Items.Any(i => i.ImageFile == item.ImageFile)) TryDelete(item.ImageFile);
    }

    public void ClearUnpinned()
    {
        foreach (var i in Items.Where(i => !i.Pinned).ToList()) Remove(i);
    }

    /// <summary>Empties the whole history – pinned entries and the paste queue included.</summary>
    public void ClearAll()
    {
        foreach (var i in Items.ToList()) Remove(i);
        Queue.Clear();
        _save.Run(Save);
    }

    public void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        _save.Run(Save);
    }

    // ---------- persistence ----------

    private void Load()
    {
        var list = Storage.Load<List<ClipItem>>("clipboard.json");
        foreach (var i in list)
        {
            if (!App.Settings.ClipboardPersist && !i.Pinned) continue;
            if (i.Kind == ClipKind.Image && (i.ImageFile == null || !File.Exists(i.ImageFile))) continue;
            Items.Add(i);
        }
    }

    /// <summary>Off only while a test shows sample entries – the user's history on disk stays untouched.</summary>
    public bool Persist { get; set; } = true;

    public void Save()
    {
        if (!Persist) return;
        if (App.Settings.ClipboardPersist) Storage.Save("clipboard.json", Items.ToList());
        else Storage.Save("clipboard.json", Items.Where(i => i.Pinned).ToList());
    }

    public void Shutdown()
    {
        _save.Flush();
        Save();
        _hook.Uninstall();
        if (!App.Settings.ClipboardPersist)
        {
            foreach (var f in Directory.GetFiles(ClipDir, "*.png"))
                if (!Items.Any(i => i.Pinned && i.ImageFile == f)) TryDelete(f);
        }
    }

    private static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }
}
