using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IslandHub.Core;

namespace IslandHub.Services;

/// <summary>One tile in the "Spiele" tab: a game or program the user added.</summary>
public sealed class LaunchItem : ObservableObject
{
    private string _name;
    private ImageSource _icon;
    private bool _editing;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>File (exe, shortcut, any document) or a URI like steam://rungameid/123.</summary>
    public string Target { get; set; }
    public string SteamAppId { get; set; }
    /// <summary>Picture saved by IslandHub (launcher\&lt;id&gt;.png).</summary>
    public string IconFile { get; set; }
    /// <summary>true = full-bleed picture (game cover), false = program icon on a tile.</summary>
    public bool IsCover { get; set; }
    /// <summary>"steamlib" = the Steam tile: a click opens the library, a right-click lists the games to start.</summary>
    public string Kind { get; set; }

    [JsonIgnore] public bool IsSteamLibrary => Kind == "steamlib";
    /// <summary>A web address (opens in the browser).</summary>
    [JsonIgnore] public bool IsLink => Kind == "link";
    [JsonIgnore] public string Tip => IsSteamLibrary ? "Steam · Rechtsklick: Spiel auswählen und starten" : IsLink ? $"{Name} · {Target}" : Name;

    [JsonIgnore] public bool IsIcon => !IsCover;
    [JsonIgnore] public bool Editing { get => _editing; set => Set(ref _editing, value); }
    [JsonIgnore]
    public ImageSource Icon
    {
        get
        {
            if (_icon != null || IconFile == null || !File.Exists(IconFile)) return _icon;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(IconFile);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bi.EndInit();
                bi.Freeze();
                _icon = bi;
            }
            catch { }
            return _icon;
        }
    }
    public void ResetIcon() { _icon = null; Raise(nameof(Icon)); }
}

/// <summary>"Spiele" tab: games and programs the user pinned, started with one click.</summary>
public sealed class LauncherService
{
    private static readonly string IconDir = Path.Combine(Storage.Dir, "launcher");
    public ObservableCollection<LaunchItem> Items { get; } = new();

    public void Load()
    {
        foreach (var i in Storage.Load<List<LaunchItem>>("launcher.json")) Items.Add(i);
    }

    /// <summary>false = render test: nothing is written to launcher.json.</summary>
    public bool Persist { get; set; } = true;

    public void Save() { if (Persist) Storage.Save("launcher.json", Items.ToList()); }

    /// <summary>Adds files (exe, .lnk, .url – Steam shortcuts become Steam games). Returns how many were new.</summary>
    public int AddFiles(IEnumerable<string> paths)
    {
        int n = 0;
        foreach (var p in paths)
        {
            try { if (AddFile(p)) n++; }
            catch (Exception ex) { App.Log(ex); }
        }
        if (n > 0) Save();
        return n;
    }

    private bool AddFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path))) return false;
        // Steam's desktop shortcuts are .url files with steam://rungameid/<id>
        if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            var url = File.ReadLines(path).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..];
            if (url != null && Regex.Match(url, @"^steam://rungameid/(\d+)") is { Success: true } m)
                return AddSteam(m.Groups[1].Value, System.IO.Path.GetFileNameWithoutExtension(path), save: false) != null;
        }
        if (Items.Any(i => string.Equals(i.Target, path, StringComparison.OrdinalIgnoreCase))) return false;
        var item = new LaunchItem { Target = path, Name = NiceName(path) };
        item.IconFile = SaveIcon(item.Id, ShellImage.Get(path, 128), false);
        Items.Add(item);
        return true;
    }

    /// <summary>Adds an installed Steam game (cover picture from Steam's library cache).</summary>
    public LaunchItem AddSteam(string appId, string name, bool save = true)
    {
        if (Items.FirstOrDefault(i => i.SteamAppId == appId) is { } had) return had;
        var item = new LaunchItem { SteamAppId = appId, Target = $"steam://rungameid/{appId}", Name = name };
        var cover = App.Hub?.Steam?.CoverFile(appId);
        BitmapSource pic = null;
        if (cover != null)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(cover);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                pic = bi;
            }
            catch { }
        }
        item.IsCover = pic != null;
        item.IconFile = pic != null ? SaveIcon(item.Id, SquareCrop(pic, 160), true) : SaveIcon(item.Id, ShellImage.Get(SteamExe(), 128), false);
        Items.Add(item);
        if (save) Save();
        return item;
    }

    /// <summary>
    /// The Steam tile (user wish 2026-11-14): one tile for the whole library – right-click shows the installed games,
    /// the last played first. Added once on its own when Steam is installed; returns null without Steam.
    /// </summary>
    public LaunchItem AddSteamLibrary()
    {
        if (Items.FirstOrDefault(i => i.IsSteamLibrary) is { } had) return had;
        var exe = SteamExe();
        if (exe == null || !File.Exists(exe)) return null;
        var item = new LaunchItem { Kind = "steamlib", Name = "Steam", Target = "steam://open/games" };
        item.IconFile = SaveIcon(item.Id, ShellImage.Get(exe, 128), false);
        Items.Insert(0, item);
        Save();
        return item;
    }

    public static bool SteamInstalled => SteamExe() is { } e && File.Exists(e);

    /// <summary>A web address as a valid http(s) link, else null ("youtube.com" becomes https://youtube.com).</summary>
    public static Uri ParseLink(string text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains(' ')) return null;
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Regex.IsMatch(text, @"^[\w-]+(\.[\w-]+)+(/.*)?$")) return null;
            text = "https://" + text;
        }
        return Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;
    }

    /// <summary>
    /// A link as a tile (user wish 2026-11-15): the site's own icon if it has a big one (asked from the site itself,
    /// nothing goes to a third party), else a coloured tile with the first letter. Returns null if it was already there.
    /// </summary>
    public async Task<LaunchItem> AddLinkAsync(Uri url, string name = null)
    {
        var target = url.AbsoluteUri;
        if (Items.Any(i => string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase))) return null;
        var host = url.Host.StartsWith("www.") ? url.Host[4..] : url.Host;
        var item = new LaunchItem { Kind = "link", Target = target, Name = string.IsNullOrWhiteSpace(name) ? NiceHost(host) : name.Trim() };
        BitmapSource pic = await SiteIconAsync(url);
        item.IsCover = true;
        item.IconFile = SaveIcon(item.Id, pic ?? LetterTile(item.Name), true);
        Items.Add(item);
        Save();
        return item;
    }

    /// <summary>"youtube.com" → "YouTube"-ish: the first part, capitalised.</summary>
    private static string NiceHost(string host)
    {
        var first = host.Split('.')[0];
        return first.Length > 0 ? char.ToUpperInvariant(first[0]) + first[1..] : host;
    }

    private static readonly System.Net.Http.HttpClient Web = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>The site's apple-touch-icon (180 px) – only from the site itself; null if there is none.</summary>
    private static async Task<BitmapSource> SiteIconAsync(Uri url)
    {
        foreach (var path in new[] { "/apple-touch-icon.png", "/apple-touch-icon-precomposed.png" })
        {
            try
            {
                var bytes = await Web.GetByteArrayAsync(new Uri(url, path));
                if (bytes.Length < 200) continue;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(bytes);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 160;
                bi.EndInit();
                bi.Freeze();
                if (bi.PixelWidth >= 64) return bi;
            }
            catch { }
        }
        return null;
    }

    /// <summary>A rounded colour tile with the first letter (a steady colour per name).</summary>
    private static BitmapSource LetterTile(string name)
    {
        var palette = new[] { 0x0A84FF, 0x30B0C7, 0x34C759, 0xFF9500, 0xAF52DE, 0xFF2D55, 0x5E5CE6, 0xA2845E };
        int h = 0;
        foreach (var ch in name ?? "") h = h * 31 + ch;
        int c = palette[Math.Abs(h % palette.Length)];
        var col = Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var g = new LinearGradientBrush(col, Color.FromRgb((byte)(col.R * 0.7), (byte)(col.G * 0.7), (byte)(col.B * 0.7)), 90);
            dc.DrawRectangle(g, null, new Rect(0, 0, 160, 160));
            var letter = string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
            var ft = new FormattedText(letter, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 84, Brushes.White, 1.0);
            dc.DrawText(ft, new Point((160 - ft.Width) / 2, (160 - ft.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(160, 160, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    public void Remove(LaunchItem item)
    {
        Items.Remove(item);
        if (item.IconFile != null) try { File.Delete(item.IconFile); } catch { }
        Save();
    }

    public void Rename(LaunchItem item, string name)
    {
        name = name?.Trim();
        if (!string.IsNullOrEmpty(name)) item.Name = name;
        Save();
    }

    public void Move(LaunchItem item, int index)
    {
        int from = Items.IndexOf(item);
        index = Math.Clamp(index, 0, Items.Count - 1);
        if (from < 0 || from == index) return;
        Items.Move(from, index);
        Save();
    }

    public bool Launch(LaunchItem item)
    {
        try
        {
            var psi = new ProcessStartInfo(item.Target) { UseShellExecute = true };
            if (item.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && System.IO.Path.GetDirectoryName(item.Target) is { } dir)
                psi.WorkingDirectory = dir;
            Process.Start(psi);
            return true;
        }
        catch (Exception ex) { App.Log(ex); return false; }
    }

    public bool Exists(LaunchItem item) => item.SteamAppId != null || item.IsSteamLibrary || item.IsLink || File.Exists(item.Target) || Directory.Exists(item.Target);

    // ---------- helpers ----------

    private static string NiceName(string path)
    {
        var file = System.IO.Path.GetFileNameWithoutExtension(path.TrimEnd('\\'));
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                foreach (var s in new[] { v.ProductName, v.FileDescription })
                    if (!string.IsNullOrWhiteSpace(s) && s.Length <= 24 && !s.Contains("Bootstrap", StringComparison.OrdinalIgnoreCase)) return s.Trim();
            }
            catch { }
        }
        return string.IsNullOrEmpty(file) ? path : file;
    }

    private static string SteamExe()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return (k?.GetValue("SteamExe") as string)?.Replace('/', '\\');
        }
        catch { return null; }
    }

    /// <summary>Middle square of a picture, scaled to size×size.</summary>
    private static BitmapSource SquareCrop(BitmapSource src, int size)
    {
        int s = Math.Min(src.PixelWidth, src.PixelHeight);
        var crop = new CroppedBitmap(src, new Int32Rect((src.PixelWidth - s) / 2, (src.PixelHeight - s) / 2, s, s));
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(crop, new Rect(0, 0, size, size));
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static string SaveIcon(string id, BitmapSource img, bool cover)
    {
        if (img == null) return null;
        try
        {
            Directory.CreateDirectory(IconDir);
            var file = System.IO.Path.Combine(IconDir, id + ".png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using var fs = File.Create(file);
            enc.Save(fs);
            return file;
        }
        catch (Exception ex) { App.Log(ex); return null; }
    }
}

/// <summary>Big, sharp shell pictures (program icons up to 256 px) with alpha, via IShellItemImageFactory.</summary>
public static class ShellImage
{
    public static BitmapSource Get(string path, int size)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
            if (item is IShellItemImageFactory f && f.GetImage(new SIZE { cx = size, cy = size }, 0x1 | 0x4 /* BIGGERSIZEOK | ICONONLY */, out var hbmp) == 0)
            {
                try { return FromHBitmap(hbmp); }
                finally { DeleteObject(hbmp); }
            }
        }
        catch { }
        return ShellIcon.Get(path) as BitmapSource;
    }

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
