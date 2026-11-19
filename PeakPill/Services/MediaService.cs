using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using PeakPill.Core;
using MediaManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace PeakPill.Services;

/// <summary>Now-playing info from any app that reports to Windows media controls (Spotify, browsers, VLC, …).</summary>
public sealed class MediaService : ObservableObject
{
    private MediaManager _mgr;
    private MediaSession _session;
    private string _title, _artist, _app;
    private ImageSource _cover;
    private Color _coverColor = Colors.Transparent;
    private bool _playing, _hasMedia;
    private TimeSpan _position, _duration;
    private DateTimeOffset _posUpdated;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    public string AppName { get => _app; private set => Set(ref _app, value); }
    public ImageSource Cover { get => _cover; private set => Set(ref _cover, value); }
    public Color CoverColor { get => _coverColor; private set => Set(ref _coverColor, value); }
    public bool IsPlaying { get => _playing; private set => Set(ref _playing, value); }
    public bool HasMedia { get => _hasMedia; private set => Set(ref _hasMedia, value); }
    public TimeSpan Duration { get => _duration; private set => Set(ref _duration, value); }
    public bool CanSeek => _duration.TotalSeconds > 1;

    public event Action TrackChanged;

    public TimeSpan Position
    {
        get
        {
            var p = _position;
            if (_playing && _posUpdated != default) p += DateTimeOffset.Now - _posUpdated;
            if (_duration > TimeSpan.Zero && p > _duration) p = _duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    /// <summary>
    /// The players Windows knows. Fetched only when Windows reports a change: every GetSessions() call creates new
    /// WinRT wrappers that the finalizer must release later – the island read this list on every music update, and
    /// PeakPill crashed twice (12.11. and 15.11.2026) with an AccessViolation in WinRT.IObjectReference.Finalize.
    /// </summary>
    public List<(string Name, MediaSession Session)> Sessions { get; private set; } = new();

    private void ReadSessions()
    {
        try { Sessions = _mgr?.GetSessions().Select(s => (FriendlyApp(s.SourceAppUserModelId), s)).ToList() ?? new(); }
        catch { Sessions = new(); }
    }

    public async Task InitAsync()
    {
        try
        {
            _mgr = await MediaManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => Ui.Run(() => Attach(_mgr.GetCurrentSession()));
            _mgr.SessionsChanged += (_, _) => Ui.Run(() =>
            {
                ReadSessions();
                if (_session == null || !Sessions.Any(x => x.Session.SourceAppUserModelId == _session.SourceAppUserModelId)) Attach(_mgr.GetCurrentSession());
            });
            ReadSessions();
            Attach(_mgr.GetCurrentSession());
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public void SelectSession(MediaSession s) => Attach(s);

    /// <summary>Render test: made-up music is set – the real players are ignored from then on.</summary>
    private bool _faked;

    private void Attach(MediaSession s)
    {
        if (_faked) return;
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnProps;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }
        _session = s;
        if (s == null)
        {
            HasMedia = false; IsPlaying = false; Title = Artist = AppName = null; Cover = null;
            TrackChanged?.Invoke();
            return;
        }
        s.MediaPropertiesChanged += OnProps;
        s.PlaybackInfoChanged += OnPlayback;
        s.TimelinePropertiesChanged += OnTimeline;
        AppName = FriendlyApp(s.SourceAppUserModelId);
        _ = RefreshProps();
        RefreshPlayback();
        RefreshTimeline();
    }

    private void OnProps(MediaSession s, MediaPropertiesChangedEventArgs e) => Ui.Run(() => { if (s == _session && !_faked) _ = RefreshProps(); });
    private void OnPlayback(MediaSession s, PlaybackInfoChangedEventArgs e) => Ui.Run(() => { if (s == _session && !_faked) RefreshPlayback(); });
    private void OnTimeline(MediaSession s, TimelinePropertiesChangedEventArgs e) => Ui.Run(() => { if (s == _session && !_faked) RefreshTimeline(); });

    private async Task RefreshProps()
    {
        var s = _session;
        if (s == null) return;
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            if (s != _session || p == null) return;
            bool changed = p.Title != Title || p.Artist != Artist;
            Title = string.IsNullOrWhiteSpace(p.Title) ? L.T("Unbekannter Titel") : p.Title;
            Artist = string.IsNullOrWhiteSpace(p.Artist) ? p.AlbumArtist : p.Artist;
            HasMedia = true;

            long before = CoverHash;
            var cover = await LoadThumb(p);
            if (s != _session) return;
            // players often send the new title with the OLD picture first – the hash tells whether the picture really changed
            var (avg, hash) = cover is BitmapSource bs ? Analyze(bs) : (Colors.Transparent, 0L);
            CoverHash = hash;
            Cover = cover;
            CoverColor = avg;
            if (changed) TrackChanged?.Invoke();
            if (changed && hash == before) _ = ChaseCover(s, Title, before);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static async Task<ImageSource> LoadThumb(GlobalSystemMediaTransportControlsSessionMediaProperties p)
    {
        if (p?.Thumbnail == null) return null;
        using var ras = await p.Thumbnail.OpenReadAsync();
        using var st = ras.AsStreamForRead();
        var ms = new MemoryStream();
        await st.CopyToAsync(ms);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = ms;
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = 300;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    /// <summary>
    /// A new title still with the old picture: Windows often gets the new one a moment later and does not always say so
    /// – ask again every quarter second (up to 4 s) until it is there (user report 2026-11-19: "das richtige Cover kommt
    /// erst viel zu spät").
    /// </summary>
    private async Task ChaseCover(MediaSession s, string title, long oldHash)
    {
        for (int i = 0; i < 16; i++)
        {
            await Task.Delay(250);
            if (s != _session || Title != title || CoverHash != oldHash) return;
            try
            {
                var p = await s.TryGetMediaPropertiesAsync();
                var cover = await LoadThumb(p);
                if (s != _session || Title != title || cover is not BitmapSource bs) continue;
                var (avg, hash) = Analyze(bs);
                if (hash == oldHash || hash == 0) continue;
                CoverHash = hash;
                Cover = cover;
                CoverColor = avg;
                return;
            }
            catch { }
        }
    }

    private void RefreshPlayback()
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            HasMedia = _session != null;
        }
        catch { }
    }

    private void RefreshTimeline()
    {
        try
        {
            var t = _session?.GetTimelineProperties();
            if (t == null) return;
            Duration = t.EndTime - t.StartTime;
            _position = t.Position - t.StartTime;
            _posUpdated = t.LastUpdatedTime;
            Raise(nameof(CanSeek));
        }
        catch { }
    }

    public async void PlayPause() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
    public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }

    public async void Seek(TimeSpan pos)
    {
        try
        {
            if (_session == null) return;
            _position = pos; _posUpdated = DateTimeOffset.Now;
            await _session.TryChangePlaybackPositionAsync(pos.Ticks);
        }
        catch { }
    }

    public static string FriendlyApp(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        var s = aumid;
        if (s.Contains('!')) s = s[(s.LastIndexOf('!') + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        if (s.Contains('.')) s = s[(s.LastIndexOf('.') + 1)..];
        return s.ToLowerInvariant() switch
        {
            "msedge" => "Microsoft Edge",
            "chrome" => "Google Chrome",
            "firefox" => "Firefox",
            "spotify" or "spotifymusic" => "Spotify",
            "vlc" => "VLC",
            "app" => "Apple Music",
            _ => s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s
        };
    }

#if DEBUG
    /// <summary>Test only: pretend the player reports this song (title first, the picture may follow later).</summary>
    /// <summary>Test only: playing / paused as the player would report it.</summary>
    public void FakePlaying(bool playing) => IsPlaying = playing;

    /// <summary>Render test only: no music at all (the quiet island).</summary>
    public void FakeNone() { _faked = true; HasMedia = false; IsPlaying = false; Title = Artist = AppName = null; Cover = null; }

    public void Fake(string title, string artist, BitmapSource cover, bool track)
    {
        _faked = true;
        bool changed = title != Title;
        Title = title; Artist = artist; AppName = "Spotify"; HasMedia = true; IsPlaying = true;
        var (avg, hash) = cover != null ? Analyze(cover) : (Colors.Transparent, 0L);
        CoverHash = hash;
        Cover = cover;
        CoverColor = avg;
        if (changed && track) TrackChanged?.Invoke();
    }
#endif

    /// <summary>Picture fingerprint of the current cover (0 = none): same picture = same number.</summary>
    public long CoverHash { get; private set; }

    /// <summary>Average colour (favouring saturated pixels) and a coarse fingerprint of a 16×16 copy.</summary>
    private static (Color Avg, long Hash) Analyze(BitmapSource src)
    {
        try
        {
            var scaled = new TransformedBitmap(src, new ScaleTransform(16.0 / src.PixelWidth, 16.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            long r = 0, g = 0, b = 0, n = 0, hash = 17;
            for (int i = 0; i < px.Length; i += 4)
            {
                int bb = px[i], gg = px[i + 1], rr = px[i + 2];
                int max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
                int weight = 1 + (max - min) / 16; // favour saturated pixels
                r += rr * weight; g += gg * weight; b += bb * weight; n += weight;
                // coarse (>> 4): re-encoded copies of the same picture still match
                hash = hash * 31 + ((rr >> 4) << 8 | (gg >> 4) << 4 | (bb >> 4));
            }
            if (n == 0) return (Colors.Transparent, hash);
            return (Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n)), hash == 0 ? 1 : hash);
        }
        catch { return (Colors.Transparent, 0); }
    }
}
