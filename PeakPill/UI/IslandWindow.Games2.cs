using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// Games (user's picks 2026-11-18): 36a the time played today in the game pill + a reminder, 36b what was played on the
/// Spiele page · 37 sales on the Steam wishlist · 38 FPS from RivaTuner · 39 instant replay through OBS.
/// </summary>
public partial class IslandWindow
{
    private bool _playView;
    private DateTime _gameToldOn;

    private void WireGames2()
    {
        PlaySwitch.MouseEnter += (_, _) => PlaySwitch.Foreground = Brushes.White;
        PlaySwitch.MouseLeave += (_, _) => PlaySwitch.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
        PlaySwitch.MouseLeftButtonUp += (_, e) => { e.Handled = true; _playView = !_playView; RefreshPlayView(true); };
        RefreshPlayView(false);
        H.Usage.Tick += () => Ui.Run(GamesTick);
        After(90_000, () => _ = CheckWishlist()); // a while after the start, then once a day
    }

    // ---------- 36a / 38 · the game pill ----------

    /// <summary>Called by UpdatePill in the game mode: FPS (if RivaTuner tells them) in front, the time played at the end.</summary>
    private void GamePillExtras()
    {
        int? fps = null;
        try
        {
            var h = Native.GetForegroundWindow();
            if (h != IntPtr.Zero) { GetWindowThreadProcessId(h, out var pid); fps = RtssFps.For(pid); }
        }
        catch { }
        var cpu = LeadText.Text;
        LeadText.Inlines.Clear();
        if (fps is { } f)
        {
            var color = f >= 60 ? (Brush)FindResource("Green") : f >= 30 ? (Brush)FindResource("Orange") : (Brush)FindResource("Danger");
            LeadText.Inlines.Add(new Run($"{f}") { Foreground = color });
            LeadText.Inlines.Add(new Run(" fps   ") { Foreground = (Brush)FindResource("SubText") });
        }
        LeadText.Inlines.Add(new Run(cpu));
        double played = H.Usage.GameSecondsToday;
        if (played >= 60 && H.Stats.HasGpu)
        {
            var t = TimeSpan.FromSeconds(played);
            TrailText.Text = TrailText.Text.Split("  RAM")[0] + $"  {(int)t.TotalHours}:{t.Minutes:00}";
        }
    }

    private void GamesTick()
    {
        // 36a: once a day, after the chosen time played – a quiet reminder, no wobble
        if (S.GameReminderMinutes > 0 && _gameToldOn != DateTime.Today && H.Usage.GameSecondsToday >= S.GameReminderMinutes * 60)
        {
            _gameToldOn = DateTime.Today;
            var t = TimeSpan.FromSeconds(H.Usage.GameSecondsToday);
            _hintHoldMs = 4500;
            ShowHint("gamecontroller", L.T($"{(int)t.TotalHours} h {t.Minutes:00} gespielt – kurze Pause?"), (Brush)FindResource("Orange"), key: "gametime");
        }
        if (_playView && _state == State.Expanded && _tab == "Games") RefreshPlayView(false);
        // 37: once a day
        if (S.WishlistSales && S.WishlistCheckedOn.Date != DateTime.Today && DateTime.Now.Hour >= 10) _ = CheckWishlist();
    }

    // ---------- 36b · the Spiele page ----------

    private void RefreshPlayView(bool animate)
    {
        PlaySwitch.Text = _playView ? L.T("Spiele") : L.T("Spielzeit");
        PlayView.Visibility = _playView ? Visibility.Visible : Visibility.Collapsed;
        GamesScroll.Visibility = _playView ? Visibility.Collapsed : Visibility.Visible;
        if (!_playView) return;
        var games = H.Usage.GamesToday();
        double total = games.Sum(g => g.Seconds);
        PlayHead.Text = total >= 60 ? L.T($"Heute gespielt · {Hm(total)}") : L.T("Heute noch nicht gespielt");
        PlayRows.Children.Clear();
        if (games.Count == 0) return;
        double max = games[0].Seconds;
        int i = 0;
        foreach (var (name, secs) in games.Take(3))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var time = new TextBlock { Text = Hm(secs), FontSize = 10.5, Width = 46, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            time.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
            DockPanel.SetDock(time, Dock.Right);
            row.Children.Add(time);
            row.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = Brushes.White, Width = 130, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var bar = new Border
            {
                Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Width = Math.Max(3, 150 * secs / max), Background = new SolidColorBrush(Color.FromRgb(0xBF, 0x5A, 0xF2)), RenderTransform = new ScaleTransform(1, 1),
            };
            row.Children.Add(bar);
            PlayRows.Children.Add(row);
            if (animate && S.Animations != AnimationLevel.Aus)
                ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { BeginTime = TimeSpan.FromMilliseconds(i * 70), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            i++;
        }
    }

    // ---------- 37 · the Steam wishlist ----------

    private bool _wishlistBusy;

    private async Task CheckWishlist()
    {
        if (!S.WishlistSales || _wishlistBusy || S.WishlistCheckedOn.Date == DateTime.Today) return;
        var id = SteamWishlist.LocalSteamId();
        if (id == null) return;
        _wishlistBusy = true;
        try
        {
            var sales = await SteamWishlist.SalesAsync(id);
            S.WishlistCheckedOn = DateTime.Today;
            var fresh = sales.Where(s => !S.WishlistTold.Contains($"{s.AppId}:{s.Percent}")).OrderByDescending(s => s.Percent).ToList();
            foreach (var s in fresh) S.WishlistTold.Add($"{s.AppId}:{s.Percent}");
            if (S.WishlistTold.Count > 300) S.WishlistTold.RemoveRange(0, S.WishlistTold.Count - 300);
            App.SaveSettings();
            foreach (var s in fresh.Take(3))
            {
                var sale = s;
                var p = new PeekInfo
                {
                    Key = "wish:" + s.AppId, Icon = "gamecontroller", GlyphBrush = (Brush)FindResource("Green"),
                    Source = L.T("Steam-Wunschliste"), SourceIcon = "gamecontroller", SourceBrush = (Brush)FindResource("Green"),
                    Title = s.Name, Text = $"−{s.Percent} % · {s.Price}" + (string.IsNullOrEmpty(s.Was) ? "" : L.T($" statt {s.Was}")), TimeoutMs = 10000,
                    Click = () => Launch($"https://store.steampowered.com/app/{sale.AppId}"),
                };
                p.Action(L.T("Im Shop"), () => Launch($"https://store.steampowered.com/app/{sale.AppId}"), true);
                ShowPeek(p);
            }
        }
        catch (Exception ex) { App.Log(ex); } // a private wishlist or no internet: next time
        finally { _wishlistBusy = false; }
    }

    // ---------- 39 · instant replay ----------

    private async Task InstantReplay()
    {
        if (!H.Obs.Connected) { ShowHint("record", L.T("OBS läuft nicht"), (Brush)FindResource("SubText"), key: "replay"); return; }
        ShowHint("rotate.left", L.T("Speichert die letzten Sekunden …"), (Brush)FindResource("Danger"), key: "replay");
        SpinHintIcon();
        var (started, file) = await H.Obs.SaveReplayAsync();
        if (started)
        {
            ShowHint("record", L.T("Replay-Puffer läuft – ab jetzt speicherbar"), (Brush)FindResource("Green"), key: "replay");
            return;
        }
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) { ShowHint("xmark", L.T("OBS hat nichts gespeichert"), (Brush)FindResource("Danger"), key: "replay"); return; }
        H.Shelf.Add(new[] { file });
        var p = new PeekInfo
        {
            Key = "replay", Icon = "record", GlyphBrush = (Brush)FindResource("Danger"),
            Source = "Instant Replay", SourceIcon = "record", SourceBrush = (Brush)FindResource("Danger"),
            Title = Path.GetFileName(file), Text = L.T("Auch in der Ablage – zum Herausziehen"), TimeoutMs = 8000,
            Click = () => Launch(file),
        };
        p.Action(L.T("Ordner"), () => Process.Start("explorer.exe", $"/select,\"{file}\""));
        p.Action(L.T("Abspielen"), () => Launch(file), true);
        ShowPeek(p);
    }

    /// <summary>The rewind arrow turns while OBS saves.</summary>
    private void SpinHintIcon()
    {
        if (S.Animations == AnimationLevel.Aus) return;
        var r = new RotateTransform();
        HintIcon.RenderTransform = r;
        r.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, -720, TimeSpan.FromMilliseconds(1600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        After(1700, () => HintIcon.RenderTransform = HintIconScale);
    }
}
