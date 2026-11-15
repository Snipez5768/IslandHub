using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IslandHub.Core;

namespace IslandHub.Services;

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
        ClipKind.Image => IsScreenshot ? "Screenshot" : "Bild",
        ClipKind.Files => Files == null ? "" :
            (Files.Count == 1 ? Path.GetFileName(Files[0]) : $"{Files.Count} Dateien: " + string.Join(", ", Files.Take(4).Select(Path.GetFileName))),
        _ => Shorten(Text)
    };

    [JsonIgnore]
    public string Meta
    {
        get
        {
            var parts = new List<string> { Relative(Time) };
            if (Kind == ClipKind.Image && ImageWidth > 0) parts.Add($"{ImageWidth}×{ImageHeight}");
            if (Kind is ClipKind.Text && Text != null) parts.Add($"{Text.Length} Zeichen");
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
                case ClipKind.Files: parts.Add(Files is { Count: > 1 } ? $"{Files.Count} Dateien" : "Datei"); break;
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
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} Min";
            if (Time.Date == DateTime.Today) return $"{(int)d.TotalHours} Std";
            if (Time.Date == DateTime.Today.AddDays(-1)) return "gestern";
            return Time.ToString("dd.MM.");
        }
    }

    /// <summary>Section in the list: Angeheftet · Heute · Gestern · Diese Woche · Älter.</summary>
    [JsonIgnore]
    public string DayGroup =>
        Pinned ? "Angeheftet"
        : Time.Date == DateTime.Today ? "Heute"
        : Time.Date == DateTime.Today.AddDays(-1) ? "Gestern"
        : Time.Date > DateTime.Today.AddDays(-7) ? "Diese Woche"
        : "Älter";

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
        if (d.TotalSeconds < 45) return "gerade eben";
        if (d.TotalMinutes < 60) return $"vor {(int)d.TotalMinutes} Min";
        if (d.TotalHours < 24 && t.Date == DateTime.Today) return $"heute {t:HH:mm}";
        if (t.Date == DateTime.Today.AddDays(-1)) return $"gestern {t:HH:mm}";
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
    /// <summary>Raised for every new history entry; the bool is true when the user copied it (Ctrl+C), false for IslandHub's own adds.</summary>
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
        win.Received += (msg, _, _) => { if (msg == Native.WM_CLIPBOARDUPDATE) OnClipboardUpdate(); };
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
            if (Queue.Count > 1) SetClipboard(Queue[0]);
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
        // Give the target app a moment to read the clipboard before we swap in the next item.
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (Queue.Count == 0) return;
            Queue.RemoveAt(0);
            // the island says it once: pasted – and what comes next
            if (Queue.Count > 0)
            {
                SetClipboard(Queue[0]);
                var next = Queue[0].Title ?? "";
                if (next.Length > 24) next = next[..23].TrimEnd() + "…";
                Notice?.Invoke($"Eingefügt · als Nächstes: {next}");
            }
            else Notice?.Invoke("Eingefügt · Stapel leer");
        };
        t.Start();
    }

    /// <summary>Queue test only: lets the test's own (injected) Ctrl+V count as a paste.</summary>
    public void AcceptInjectedPasteForTest() => _hook.AcceptInjected = true;
    /// <summary>Queue test only: what happens right after the user pressed Ctrl+V.</summary>
    public void SimulatePasteForTest() => OnPasteDetected();
    public bool HookRunningForTest => _hook.Installed;

    public void RemoveFromQueue(ClipItem item)
    {
        bool wasHead = Queue.Count > 0 && Queue[0] == item;
        Queue.Remove(item);
        if (wasHead && Queue.Count > 0) SetClipboard(Queue[0]);
    }

    public void ClearQueue() => Queue.Clear();

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
