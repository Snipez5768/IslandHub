using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// Music page additions (user wish 2026-11-16): the heart (Spotify), the song text (any player), and the queue with
/// two modes – Spotify's own (view + add) and the island's own (sort freely, handed to Spotify song by song).
/// </summary>
public partial class IslandWindow
{
    private bool _lyricsOpen;
    private List<LyricLine> _lyrics = new();
    private string _lyricsFor;
    private int _lyricsIdx = int.MinValue;
    private AxisReorder _islandReorder;

    private bool IslandQueueMode => S.SpotifyQueueMode == "island";

    // ---------- heart ----------

    /// <summary>Called with every music update: the heart shows for Spotify when connected.</summary>
    private void UpdateMusicExtras()
    {
        MusicLikeBtn.Visibility = IsSpotify && H.Spotify.Connected ? Visibility.Visible : Visibility.Collapsed;
        bool liked = H.Spotify.Liked;
        Ext.SetIcon(MusicLikeBtn, liked ? "heart.fill" : "heart");
        if (liked) MusicLikeBtn.Foreground = (Brush)FindResource("Danger"); else MusicLikeBtn.ClearValue(ForegroundProperty);
        MusicLikeBtn.ToolTip = H.Spotify.NeedsReconnect ? L.T("Zum Liken Spotify einmal neu verbinden (Einstellungen)") : liked ? L.T("Aus Lieblingssongs entfernen") : L.T("Zu Lieblingssongs");
        if (_lyricsOpen) UpdateLyrics();
    }

    private async void MusicLike_Click(object sender, RoutedEventArgs e)
    {
        if (H.Spotify.NeedsReconnect)
        {
            ShowPeek(new PeekInfo { Icon = "heart", Title = L.T("Spotify neu verbinden"), Text = L.T("Fürs Liken und Hinzufügen braucht PeakPill einmal neue Rechte."), TimeoutMs = 7000 }
                .Action(L.T("Verbinden"), () => _ = H.Spotify.SignInAsync(), true));
            return;
        }
        bool wasLiked = H.Spotify.Liked;
        if (!await H.Spotify.ToggleLikeAsync()) { MusicLikeBtn.ToolTip = L.T("Spotify hat nicht reagiert"); return; }
        UpdateMusicExtras();
        if (!wasLiked && S.Animations != AnimationLevel.Aus) Pop(MusicLikeBtn); // the heart pops when it fills
    }

    // ---------- song text ----------

    private void MusicLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (_queueOpen) MusicQueue_Click(null, null);
        _lyricsOpen = !_lyricsOpen;
        LyricsView.Visibility = _lyricsOpen ? Visibility.Visible : Visibility.Collapsed;
        MusicContent.VerticalAlignment = _lyricsOpen ? VerticalAlignment.Top : VerticalAlignment.Center;
        if (_lyricsOpen) MusicLyricsBtn.Foreground = (Brush)FindResource("Blue"); else MusicLyricsBtn.ClearValue(ForegroundProperty);
        AnimateSize(ExpW, TabHeight(_tab) + NotchExtra, ExpRadius, true);
        if (_lyricsOpen) _ = LoadLyricsAsync();
    }

    private async Task LoadLyricsAsync()
    {
        var m = H.Media;
        string key = m.Artist + "|" + m.Title;
        if (key == _lyricsFor) { UpdateLyrics(); return; }
        _lyricsFor = key;
        _lyrics = new();
        _lyricsIdx = int.MinValue;
        LyricsLines.Children.Clear();
        LyricsHint.Text = L.T("Songtext wird gesucht …");
        var lines = await LyricsService.GetAsync(m.Title, m.Artist, m.Duration);
        if (key != _lyricsFor) return; // the song changed meanwhile
        _lyrics = lines.Where(l => l.Text.Length > 0).ToList(); // pauses: the last line simply stays lit
        LyricsHint.Text = _lyrics.Count == 0 ? L.T("Kein Songtext gefunden.") : _lyrics[0].At == null ? L.T("Songtext (ohne Zeiten)") : L.T("Songtext · LRCLIB");
        UpdateLyrics();
    }

    /// <summary>Five lines around the one sung now; the current one white and bold, the rest dimmed.</summary>
    private void UpdateLyrics()
    {
        if (!_lyricsOpen) return;
        if (H.Media.Artist + "|" + H.Media.Title != _lyricsFor) { _ = LoadLyricsAsync(); return; }
        if (_lyrics.Count == 0) { LyricsLines.Children.Clear(); return; }
        bool timed = _lyrics[0].At != null;
        int idx = timed ? LyricsService.LineAt(_lyrics, H.Media.Position) : 0;
        if (idx == _lyricsIdx && LyricsLines.Children.Count > 0) return;
        _lyricsIdx = idx;
        LyricsLines.Children.Clear();
        int from = timed ? Math.Max(0, idx - 1) : 0;
        for (int i = from; i < Math.Min(_lyrics.Count, from + 6); i++)
        {
            bool now = timed && i == idx;
            var text = _lyrics[i].Text.Length == 0 ? "♪" : _lyrics[i].Text;
            LyricsLines.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = text, FontSize = now ? 14 : 12.5, FontWeight = now ? FontWeights.Bold : FontWeights.SemiBold,
                Foreground = now ? Brushes.White : new SolidColorBrush(Color.FromArgb(timed && i < idx ? (byte)0x40 : (byte)0x73, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 5)
            });
        }
    }

    // ---------- queue: two modes ----------

    private void ApplyQueueMode()
    {
        bool island = IslandQueueMode;
        QueueModeSpotify.Foreground = island ? (Brush)FindResource("SubText") : Brushes.White;
        QueueModeIsland.Foreground = island ? Brushes.White : (Brush)FindResource("SubText");
        IslandQueueScroll.Visibility = island ? Visibility.Visible : Visibility.Collapsed;
        IslandQueueNote.Visibility = island ? Visibility.Visible : Visibility.Collapsed;
        SpotifyQueueScroll.Visibility = island ? Visibility.Collapsed : Visibility.Visible;
        QueueHint.Text = island ? (H.Spotify.IslandQueue.Count == 0 ? "leer" : L.T($"{H.Spotify.IslandQueue.Count} Titel")) : QueueHint.Text;
        _islandReorder ??= new AxisReorder(IslandQueueItems, horizontal: false, (from, to) =>
        {
            H.Spotify.IslandQueue.Move(from, to);
            H.Spotify.SaveIslandQueue();
        });
    }

    private void QueueMode_Click(object sender, MouseButtonEventArgs e)
    {
        S.SpotifyQueueMode = (string)((FrameworkElement)sender).Tag;
        App.SaveSettings();
        ApplyQueueMode();
        if (!IslandQueueMode) _ = RefreshQueueNow();
    }

    private async void QueueSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { QueueSearch.Text = ""; QueueResults.Visibility = Visibility.Collapsed; e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (H.Spotify.NeedsReconnect) { QueueHint.Text = L.T("Spotify einmal neu verbinden"); return; }
        QueueHint.Text = L.T("sucht …");
        var hits = await H.Spotify.SearchAsync(QueueSearch.Text);
        QueueResults.ItemsSource = hits;
        QueueResults.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueHint.Text = hits.Count == 0 ? L.T("nichts gefunden") : L.T("Titel antippen zum Hinzufügen");
    }

    private async void QueueResult_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QueueItem song) return;
        QueueResults.Visibility = Visibility.Collapsed;
        QueueSearch.Text = "";
        if (IslandQueueMode)
        {
            H.Spotify.IslandQueue.Add(song);
            H.Spotify.SaveIslandQueue();
            QueueHint.Text = L.T($"{H.Spotify.IslandQueue.Count} Titel");
        }
        else
        {
            QueueHint.Text = await H.Spotify.AddToQueueAsync(song) ? L.T("hinzugefügt") : L.T("Spotify hat nicht reagiert");
            await Task.Delay(400);
            await RefreshQueueNow();
        }
    }

    private void IslandQueueRemove_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not QueueItem song) return;
        H.Spotify.IslandQueue.Remove(song);
        H.Spotify.SaveIslandQueue();
        QueueHint.Text = H.Spotify.IslandQueue.Count == 0 ? "leer" : L.T($"{H.Spotify.IslandQueue.Count} Titel");
    }

    /// <summary>Every half second: the island queue hands Spotify its next song shortly before the end.</summary>
    private void FeedIslandQueue()
    {
        if (!IsSpotify || !H.Media.IsPlaying || H.Spotify.IslandQueue.Count == 0 || !H.Media.CanSeek) return;
        _ = H.Spotify.FeedAsync(H.Media.Title, H.Media.Duration - H.Media.Position);
    }
}
