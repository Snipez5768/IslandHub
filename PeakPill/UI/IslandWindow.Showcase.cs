#if DEBUG
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>
/// The animation gallery (user wish 2026-11-18: "jede Animation einmal zeigen … und dann überarbeiten, die mir nicht
/// gefallen"): every moment of the real island is filmed with made-up content and written into one local HTML page
/// where each can be marked "gefällt" / "überarbeiten" with a note. Nothing real shows: the players, Claude's reports
/// and Windows' notifications are switched off for the run.
/// </summary>
public partial class IslandWindow
{
    private sealed record ShowClip(string Id, string Group, string Title, string What, int Ms, List<string> Frames);

    private readonly List<ShowClip> _clips = new();
    private static readonly SolidColorBrush ShowBg = new(Color.FromRgb(0x2A, 0x2E, 0x38));

    private string SnapJpeg(FrameworkElement el, double w, double h)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var rtb = new RenderTargetBitmap((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(ShowBg, null, new Rect(0, 0, w, h));
        rtb.Render(bg);
        rtb.Render(el);
        var enc = new JpegBitmapEncoder { QualityLevel = 86 };
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Starts <paramref name="act"/> and films the island meanwhile (<paramref name="count"/> pictures, every <paramref name="ms"/>).</summary>
    private async Task Rec(string id, string group, string title, string what, Func<Task> act, int count, int ms, double h = 90)
    {
        var frames = new List<string>();
        var run = act();
        for (int i = 0; i < count; i++)
        {
            frames.Add(SnapJpeg(Root, ActualWidth, h));
            await Task.Delay(ms);
        }
        try { await run; } catch (Exception ex) { App.Log(ex); }
        _clips.Add(new ShowClip(id, group, title, what, ms, frames));
    }

    private static BitmapSource ShowPic(Color a, Color b)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(a, b, 45), null, new Rect(0, 0, 64, 64));
        var r = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        r.Render(dv);
        r.Freeze();
        return r;
    }

    private async Task ShowReset()
    {
        _sticky = false;
        if (_state != State.Collapsed) Collapse();
        H.Timer.Reset();
        H.Agents.EndDemo();
        try { H.Privacy.EndDemo(); } catch { }
        _hungUpUntil = DateTime.MinValue;
        _agentDoneUntil = DateTime.MinValue;
        S.IslandEyes = false;
        UpdateEyes();
        DropEyesNow();
        H.Media.FakeNone();
        UpdatePill(true);
        await Task.Delay(800);
    }

    public async Task ShowcaseAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        _demoOnly = true;
        H.Agents.StopListening();
        H.Clipboard.Persist = false;
        H.Clipboard.Items.Clear(); // in memory only (Persist is off) – the list shows demo entries
        Root.IsHitTestVisible = false;
        bool eyes = S.IslandEyes, peek = S.TrackPeek, secs = S.ShowSeconds, confirm = S.ConfirmCopy, dev = S.DeviceInIsland, power = S.PowerInIsland, level = S.LevelInIsland;
        S.ConfirmCopy = S.DeviceInIsland = S.PowerInIsland = S.LevelInIsland = true;
        S.Animations = AnimationLevel.An;
        var coverA = ShowPic(Color.FromRgb(0xFF, 0x7A, 0x3D), Color.FromRgb(0xE0, 0x3A, 0x5A));
        var coverB = ShowPic(Color.FromRgb(0x3D, 0x8B, 0xFF), Color.FromRgb(0x6A, 0x3D, 0xE0));
        var coverC = ShowPic(Color.FromRgb(0x2E, 0xC4, 0x8A), Color.FromRgb(0x1F, 0x6E, 0xA8));
        var green = (Brush)FindResource("Green");
        var notepad = @"C:\Windows\System32\notepad.exe";

        // ---------- the island itself ----------
        await ShowReset();
        H.Media.Fake("Nachtlicht", "Lena Ost", coverA, false);
        UpdatePill(true);
        await Task.Delay(900);
        await Rec("open", "Insel", "Öffnen", "Die Insel geht auf (hier mit Musik: das Cover fliegt groß).", () => { _sticky = true; Expand("Music", false); _sticky = true; return Task.CompletedTask; }, 18, 35, 260);
        await Task.Delay(500);
        await Rec("close", "Insel", "Schließen", "Zurück zur Pille, das Cover fliegt zurück.", () => { Collapse(); return Task.CompletedTask; }, 16, 35, 260);
        await ShowReset();
        Expand(L.T("Timer"), false); _sticky = true;
        await Task.Delay(900);
        await Rec("page", "Insel", "Seitenwechsel (Karussell)", "Beide Seiten schieben sich um die ganze Breite, der Balken gleitet unter das Tab.", () => { _tabBtns["Today"].IsChecked = true; return Task.CompletedTask; }, 16, 40, 260);
        await Task.Delay(600);
        await Rec("page2", "Insel", "Seitenwechsel auf eine höhere Seite", "Größe und Seitenwechsel in einer Bewegung.", () => { _tabBtns["Clip"].IsChecked = true; return Task.CompletedTask; }, 16, 40, 300);
        await ShowReset();
        await Rec("tuck", "Insel", "Verstecken", "Die Insel rollt sich nach oben in den Rand ein.", () => { Tuck(); return Task.CompletedTask; }, 12, 40, 80);
        await Task.Delay(400);
        await Rec("untuck", "Insel", "Zurückholen", "Sie rollt sich wieder heraus und geht auf.", () => { Untuck(); return Task.CompletedTask; }, 16, 45, 260);
        await ShowReset();
        await Rec("room", "Insel", "Platz machen (Vollbild-Browser)", "Die Insel fährt nach oben weg und kommt wieder.", async () => { SetRoomAway(true); await Task.Delay(700); SetRoomAway(false); }, 26, 50, 80);

        // ---------- music ----------
        await ShowReset();
        H.Media.Fake("Nachtlicht", "Lena Ost", coverA, false);
        UpdatePill(true);
        await Task.Delay(1000);
        await Rec("song", "Musik", "Songwechsel", "Das alte Cover gleitet raus, das neue kommt rein.", () => { H.Media.Fake("Sternenstaub", "Mika Rhein", coverB, true); UpdatePill(); return Task.CompletedTask; }, 18, 40, 70);
        await Task.Delay(500);
        await Rec("pause", "Musik", "Pause", "Die Balken fallen zusammen, das Cover dimmt.", () => { H.Media.FakePlaying(false); UpdatePill(true); return Task.CompletedTask; }, 12, 45, 70);
        await Task.Delay(400);
        await Rec("play", "Musik", "Weiterspielen", "Die Balken ploppen auf, das Cover leuchtet wieder.", () => { H.Media.FakePlaying(true); UpdatePill(true); return Task.CompletedTask; }, 10, 45, 70);
        await Rec("beat", "Musik", "Cover im Bass", "Das kleine Cover gibt bei jedem Bass-Schlag nach.", async () =>
        {
            for (int i = 0; i < 24; i++) { CoverBeat(i % 6 == 0 ? 0.95 : 0.12); await Task.Delay(55); }
        }, 22, 60, 70);
        S.TrackPeek = true;
        await Rec("trackpeek", "Musik", "Neuer Titel kurz anzeigen", "Bei einem neuen Song zeigt die Insel kurz Titel und Cover.", () => { H.Media.Fake("Morgenlicht", "Ava Sund", coverC, true); UpdatePill(); return Task.CompletedTask; }, 22, 60, 130);
        S.TrackPeek = peek;

        // ---------- eyes ----------
        await ShowReset();
        S.IslandEyes = true;
        UpdateEyes();
        await Task.Delay(300);
        await Rec("eyeswake", "Augen", "Aufwachen", "Platz geht auf, die Augen ploppen geschlossen heraus, öffnen sich, blinzeln einmal.", () => { _eyesUntil = DateTime.Now.AddSeconds(90); return Task.CompletedTask; }, 26, 45, 70);
        await Task.Delay(800);
        await Rec("eyesblink", "Augen", "Blinzeln", "Ab und zu ein kurzer Lidschlag.", () => { _blinkAt = DateTime.Now; StartMotion(); return Task.CompletedTask; }, 9, 30, 70);
        await Rec("eyeslook", "Augen", "Dem Zeiger nachschauen", "Die Pupillen folgen der Maus.", async () =>
        {
            foreach (var (x, y) in new[] { (1.3, 0.0), (1.3, 1.5), (-1.3, 1.5), (-1.3, -1.5), (0.0, 0.0) }) { Glance(x, y, 140); await Task.Delay(260); }
        }, 22, 60, 70);
        await Rec("eyesyawn", "Augen", "Gähnen", "Nachts (22–7 Uhr) ab und zu, und bei schwachem Akku.", () => { EyesYawn(); return Task.CompletedTask; }, 24, 75, 70);
        await Task.Delay(300);
        await Rec("eyesstartle", "Augen", "Erschrecken", "Bei Fehlern (Download hängt, KI-Fehler).", () => { EyesStartle(); return Task.CompletedTask; }, 14, 55, 70);
        await Task.Delay(300);
        await Rec("eyesnotice", "Augen", "Zur Mitteilung schauen", "Kommt eine Mitteilung, schauen sie zur Glocke.", () => { EyesNotice(); return Task.CompletedTask; }, 12, 50, 70);
        await Task.Delay(300);
        await Rec("eyessleep", "Augen", "Einschlafen", "Lider sinken halb, dann zu; erst dann schrumpft der Platz.", () => { _eyesUntil = DateTime.MinValue; return Task.CompletedTask; }, 26, 60, 70);
        S.IslandEyes = false;
        UpdateEyes();

        // ---------- the ring stack (default) ----------
        bool side = S.SideBubbles;
        S.SideBubbles = false;
        await ShowReset();
        H.Media.Fake("Nachtlicht", "Lena Ost", coverA, false);
        UpdatePill(true);
        await Task.Delay(900);
        await Rec("ringin", "Ring-Stapel", "Ring kommt dazu", "Ein Timer startet neben der Musik: sein Ring erscheint rechts in der Insel.", () => { H.Timer.SetDurationSeconds(300); H.Timer.Start(); UpdatePill(true); return Task.CompletedTask; }, 16, 45, 70);
        await Rec("ringmore", "Ring-Stapel", "Mehrere Ringe", "Claude und ein Anruf kommen dazu – die Ringe liegen überlappend als Stapel.", async () =>
        {
            H.Agents.Demo("Demo-Aufgabe", "Liest", 1, 3, 20, AgentState.Running, "demo-show"); UpdatePill(true);
            await Task.Delay(500);
            H.Privacy.Demo((notepad, true, false, false, 3));
            await Task.Delay(1400);
            UpdatePill(true);
        }, 26, 80, 70);
        await Rec("ringfan", "Ring-Stapel", "Stapel unter dem Zeiger", "Der Stapel fächert auf, die Insel wird etwas breiter – Klick öffnet die Seite des Rings.", async () => { FanStack(true); await Task.Delay(900); FanStack(false); }, 26, 50, 70);
        await Rec("ringout", "Ring-Stapel", "Ringe gehen", "Läuft etwas nicht mehr, verschwindet sein Ring.", async () =>
        {
            try { H.Privacy.EndDemo(); } catch { }
            UpdatePill(true);
            await Task.Delay(500);
            H.Agents.EndDemo(); H.Timer.Reset(); UpdatePill(true);
        }, 18, 60, 70);
        // ---------- the old circles (setting "Kreise neben der Insel") ----------
        S.SideBubbles = true;
        await ShowReset();
        H.Media.Fake("Nachtlicht", "Lena Ost", coverA, false);
        UpdatePill(true);
        await Task.Delay(900);
        await Rec("bubble", "Kreise (Einstellung)", "Kreis entsteht", "Mit „Kreise neben der Insel“: der Timer tropft aus der Insel, mit Faden, der reißt.", () => { H.Timer.SetDurationSeconds(300); H.Timer.Start(); UpdatePill(true); return Task.CompletedTask; }, 22, 40, 70);
        await Task.Delay(500);
        await Rec("bubblehover", "Kreise (Einstellung)", "Kreis unter dem Zeiger", "Wächst leicht und zeigt, was er ist.", async () => { BubbleHover(Bubble, true); await Task.Delay(600); BubbleHover(Bubble, false); }, 16, 50, 70);
        await Rec("bubblemerge", "Kreise (Einstellung)", "Kreis verschwindet", "Er wird von der Insel zurück geschluckt.", () => { H.Timer.Reset(); UpdatePill(true); return Task.CompletedTask; }, 20, 40, 70);
        S.SideBubbles = side;

        // ---------- notices ----------
        await ShowReset();
        await Rec("volume", "Hinweise", "Lautstärke", "Die Insel wird zum Balken, die Zahl rollt mit.", async () =>
        {
            for (int i = 0; i <= 10; i++) { ShowLevel("vol", "speaker", 0.40 + i * 0.03); await Task.Delay(70); }
        }, 20, 60, 70);
        await ShowReset();
        await Rec("copy", "Hinweise", "Kopiert", "Die Insel gibt kurz nach, der Haken gleitet herein. (Dazu fliegt der Text vom Mauszeiger herein – siehe unten.)", () =>
        {
            if (H.Clipboard.AddText("Demo-Text zum Kopieren", "Demo") is ClipItem ci) OnCaptured(ci, true);
            return Task.CompletedTask;
        }, 16, 45, 70);
        await ShowReset();
        await Rec("mute", "Hinweise", "Stumm", "Der Strich zieht sich über das Mikro, die Insel ruckelt kurz.", () => { ShowHint("mic.slash", L.T("Stumm"), (Brush)FindResource("Danger"), key: "mic"); MuteMoment(); return Task.CompletedTask; }, 18, 45, 70);
        await ShowReset();
        await Rec("headphones", "Hinweise", "Kopfhörer verbunden", "Name mit Akku-Ring, der sich füllt.", () => { OnHeadphones("Kopfhörer", 0.7); return Task.CompletedTask; }, 18, 60, 70);
        await ShowReset();
        await Rec("charge", "Hinweise", "Laden", "Der Blitz füllt sich wie eine Flüssigkeit bis zum Akkustand.", () => { OnCharger(true, 0.62); return Task.CompletedTask; }, 18, 70, 70);
        await ShowReset();
        await Rec("battery", "Hinweise", "Akku schwach", "Rote Warnung mit Ring.", () => { OnBatteryLow(0.12); return Task.CompletedTask; }, 16, 70, 70);

        // ---------- notifications ----------
        await ShowReset();
        var note = new IslandNotification { Id = -77, AppId = "Demo.Chat", AppName = "Chat", Title = "Mia", Text = "Bist du schon unterwegs?", Time = DateTime.Now, Unread = true };
        await Rec("notify", "Mitteilungen", "Mitteilung kommt", "Die Glocke schwingt in der Pille, dann öffnet die Insel eine Zeile mit der Nachricht.", () => { OnNotificationArrived(note); return Task.CompletedTask; }, 30, 60, 120);
        await Rec("read", "Mitteilungen", "„Gelesen“", "Die Karte wird nach oben in die Insel gesaugt.", () => { SuckPeekUp(); EndPeek(); return Task.CompletedTask; }, 10, 40, 120);
        await ShowReset();
        OnNotificationArrived(note);
        await Task.Delay(1500);
        await Rec("detach", "Mitteilungen", "In die App gewechselt", "Die Karte löst sich und fällt weg.", () => { DetachPeek(); return Task.CompletedTask; }, 14, 45, 120);
        await ShowReset();
        await Rec("shotpeek", "Mitteilungen", "Screenshot in der Insel", "Die Insel geht nach unten auf und zeigt das Foto.", () => { ShowPeek(new PeekInfo { Key = "shot", Shot = ShowPic(Color.FromRgb(0x90, 0xB8, 0xE8), Color.FromRgb(0x30, 0x50, 0x90)), TimeoutMs = 0 }); return Task.CompletedTask; }, 20, 50, 220);
        EndPeek();

        // ---------- timer ----------
        await ShowReset();
        await Rec("timerrim", "Timer", "Timer läuft", "Die Linie läuft um die Pille, die Ziffern rollen.", () => { H.Timer.SetDurationSeconds(125); H.Timer.Start(); UpdatePill(true); return Task.CompletedTask; }, 16, 100, 70);
        H.Timer.Reset();
        await Rec("timerlast", "Timer", "Letzte Minute", "Die Linie wird rot und pulsiert langsam.", () => { H.Timer.SetDurationSeconds(40); H.Timer.Start(); UpdatePill(true); return Task.CompletedTask; }, 16, 120, 70);
        await Rec("timerring", "Timer", "Timer-Ende: Ring schließt sich", "Am Ende läuft der Ring einmal ganz zu.", () => { RimRun((Brush)FindResource("Orange"), full: true); return Task.CompletedTask; }, 16, 90, 70);
        await Rec("timerend", "Timer", "Timer abgelaufen", "Der Moment, wenn der Timer klingelt.", () => { OnTimerCompleted(); return Task.CompletedTask; }, 30, 60, 130);
        await ShowReset();
        H.Timer.SetDurationSeconds(300); H.Timer.Start();
        Expand(L.T("Timer"), false); _sticky = true;
        await Task.Delay(1000);
        await Rec("timerhome", "Timer", "Zuklappen: Ziffern fliegen in die Pille", "Die große Zeit fliegt beim Schließen an ihren Platz in der Pille.", () => { Collapse(); return Task.CompletedTask; }, 18, 40, 220);

        // ---------- Claude ----------
        await ShowReset();
        H.Media.Fake("Nachtlicht", "Lena Ost", coverA, false);
        UpdatePill(true);
        await Task.Delay(800);
        await Rec("agent", "Claude", "Claude arbeitet", "Eine Blase mit drehendem Ring neben der Musik.", () => { H.Agents.Demo("Demo-Aufgabe", "Liest", 1, 3, 20, AgentState.Running, "demo-show"); UpdatePill(true); return Task.CompletedTask; }, 22, 60, 70);
        await Rec("agentwait", "Claude", "Claude wartet auf dich", "Das Sternchen pulsiert, ein Punkt sagt „du bist dran“.", () => { H.Agents.Demo("Demo-Aufgabe", "Wartet", 2, 3, 40, AgentState.Waiting, "demo-show"); UpdatePill(true); return Task.CompletedTask; }, 18, 80, 70);
        await ShowReset();
        H.Agents.Demo("Demo-Aufgabe", "Liest", 2, 3, 30, AgentState.Running, "demo-show");
        UpdatePill(true);
        await Task.Delay(900);
        await Rec("agentdone", "Claude", "Claude ist fertig", "Sternchen dreht sich zum Haken, ein grünes Licht läuft um die Insel, dann der Fertig-Moment.", () =>
        {
            H.Agents.Demo("Demo-Aufgabe", "Fertig", 3, 3, 45, AgentState.Done, "demo-show");
            _agentDoneUntil = DateTime.Now.AddSeconds(8);
            UpdatePill(true);
            RimRun(green);
            if (H.Agents.Sessions.FirstOrDefault() is { } s) OnAgentFinished(s);
            return Task.CompletedTask;
        }, 30, 60, 130);

        // ---------- calls ----------
        await ShowReset();
        await Rec("callstart", "Anruf", "Anruf beginnt", "Ein Programm nutzt das Mikrofon: die Insel zeigt es.", () => { H.Privacy.Demo((notepad, true, false, false, 3)); return Task.CompletedTask; }, 24, 60, 110);
        await Task.Delay(1200);
        await Rec("hangup", "Anruf", "Auflegen", "Das Telefon wird rot, fliegt in die Insel, „Anruf beendet“.", () => HangUpMoment(), 24, 45, 110);
        await Task.Delay(2500);

        // ---------- more ----------
        await ShowReset();
        await Rec("wobble", "Mehr", "Erinnerung fällig", "Die Insel wackelt wie ein Wecker.", () => { Wobble(); return Task.CompletedTask; }, 16, 75, 70);
        await Rec("magnet", "Mehr", "Datei heranziehen und schlucken", "Beim Ziehen einer Datei beult sich die Insel aus, beim Loslassen schluckt sie.", async () => { MagnetBulge(20); await Task.Delay(420); IslandGulp(); }, 18, 45, 70);
        await Rec("rimrun", "Mehr", "Lichtlauf um die Insel", "Ein Licht läuft einmal herum (z. B. Claude fertig).", () => { RimRun(green); return Task.CompletedTask; }, 16, 65, 70);
        S.ShowSeconds = true;
        UpdatePill(true);
        await Task.Delay(400);
        await Rec("clock", "Mehr", "Uhr (mit Sekunden)", "Nur die Ziffer, die sich ändert, rollt.", () => Task.CompletedTask, 14, 110, 70);
        S.ShowSeconds = secs;
        UpdatePill(true);
        await Rec("typing", "Mehr", "Beim Tippen zurücktreten", "Schreibst du woanders, wird die Insel fast durchsichtig.", async () =>
        {
            System.Threading.Interlocked.Exchange(ref _lastKeyTicks, DateTime.Now.Ticks);
            SetTypingFade(true);
            await Task.Delay(700);
            SetTypingFade(false);
        }, 16, 60, 70);

        // ---------- lists in the open island ----------
        await ShowReset();
        H.Clipboard.AddText("Einkaufsliste: Milch, Brot", "Demo");
        Expand("Clip", false); _sticky = true;
        await Task.Delay(1000);
        await Rec("listin", "Listen", "Neuer Eintrag", "Er wächst herein und schiebt die anderen nach unten.", () => { H.Clipboard.AddText("Treffen um 15 Uhr im Café", "Demo"); return Task.CompletedTask; }, 12, 45, 300);
        await Task.Delay(400);
        var del = H.Clipboard.Items.FirstOrDefault();
        await Rec("listout", "Listen", "Löschen mit Rückgängig", "Der Eintrag klappt weg, die Insel wächst um die Leiste „Rückgängig“.", () => { if (del != null) DeleteClip(del); return Task.CompletedTask; }, 18, 55, 320);
        await Rec("undo", "Listen", "Rückgängig", "Der Eintrag kommt zurück, die Leiste geht weg.", () => { DoUndo(); return Task.CompletedTask; }, 14, 50, 320);
        await Rec("hop", "Listen", "Zähler hüpft", "Kommt eine Mitteilung dazu, hüpft ihr Tab.", () => { if (_tabBtns.TryGetValue("Notify", out var nb)) HopTab(nb); return Task.CompletedTask; }, 10, 40, 60);

        // ---------- settings window ----------
        await ShowReset();
        var w = new SettingsWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0 };
        w.Show();
        await Task.Delay(800);
        var content = (FrameworkElement)w.Content;
        async Task RecWin(string id, string title, string what, Action act, int count, int ms)
        {
            var frames = new List<string>();
            act();
            for (int i = 0; i < count; i++) { frames.Add(SnapJpeg(content, content.ActualWidth, content.ActualHeight)); await Task.Delay(ms); }
            _clips.Add(new ShowClip(id, "Einstellungen", title, what, ms, frames));
        }
        await RecWin("setpage", "Seitenwechsel", "Die alte Seite gleitet unter die Seitenleiste, die neue federt herein.", () => w.ShowPage("General"), 14, 45);
        await Task.Delay(500);
        var sw = (ToggleButton)w.FindName("OptFadeTyping");
        bool was = sw.IsChecked == true;
        await RecWin("setswitch", "Schalter", "Der Knopf federt hinüber statt zu springen.", () => sw.IsChecked = !was, 10, 40);
        sw.IsChecked = was; // only the look – the setting itself is not changed (no click)
        w.Close();

        // ---------- the page ----------
        await ShowReset();
        S.IslandEyes = eyes; S.ConfirmCopy = confirm; S.DeviceInIsland = dev; S.PowerInIsland = power; S.LevelInIsland = level;
        _demoOnly = false;
        Root.IsHitTestVisible = true;
        WriteGallery(Path.Combine(dir, "animationen.html"));
    }

    /// <summary>A new page from the films of an earlier one (no new filming) – e.g. after the page itself changed.</summary>
    public void RewriteGallery(string source, string file)
    {
        var html = File.ReadAllText(source, new UTF8Encoding(false));
        int a = html.IndexOf("const C=", StringComparison.Ordinal) + 8;
        int b = html.IndexOf(";\nconst NF=", StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(html[a..b]);
        _clips.Clear();
        foreach (var c in doc.RootElement.EnumerateArray())
            _clips.Add(new ShowClip(c.GetProperty("id").GetString(), c.GetProperty("group").GetString(), c.GetProperty("title").GetString(),
                                    c.GetProperty("what").GetString(), c.GetProperty("ms").GetInt32(),
                                    c.GetProperty("frames").EnumerateArray().Select(f => f.GetString()).ToList()));
        var m = System.Text.RegularExpressions.Regex.Match(html, "const STAMP=\"([^\"]+)\"");
        WriteGallery(file, m.Success ? m.Groups[1].Value : null);
    }

    private void WriteGallery(string file, string stamp = null)
    {
        var data = _clips.Select((c, i) => new { n = i + 1, id = c.Id, group = c.Group, title = c.Title, what = c.What, ms = c.Ms, frames = c.Frames }).ToList();
        var json = JsonSerializer.Serialize(data);
        var notFilmed = JsonSerializer.Serialize(new[]
        {
            new { title = "Kopieren fliegt", what = "Der kopierte Text fliegt als kleine Karte vom Mauszeiger in die Insel." },
            new { title = "Screenshot fliegt", what = "Das Foto schrumpft und fliegt vom Bereich in die Insel." },
            new { title = "Download fällt", what = "Das Symbol einer fertigen Datei fällt aus der Insel Richtung Taskleiste." },
            new { title = "Augen nachts müde", what = "Von 22 bis 7 Uhr öffnen sie nur halb und blinzeln langsamer." },
            new { title = "Augen denken", what = "Schreibt die KI, wachen sie auf und schauen nachdenklich nach oben." },
        });
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>PeakPill · Animationen</title>
<style>
:root{--bg:#f4f4f6;--card:#fff;--text:#1d1d1f;--sub:#6e6e73;--line:#e3e3e8;--acc:#0a84ff;--ok:#2fb35b;--fix:#e5484d}
@media (prefers-color-scheme:dark){:root{--bg:#111214;--card:#1c1c1f;--text:#f2f2f7;--sub:#9a9aa0;--line:#2c2c30}}
body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
header{position:sticky;top:0;z-index:5;background:var(--bg);border-bottom:1px solid var(--line);padding:14px 20px;display:flex;flex-wrap:wrap;gap:12px;align-items:center}
h1{font-size:18px;font-weight:600;margin:0 12px 0 0}
main{padding:10px 20px 60px;max-width:1500px;margin:0 auto}
h2{font-size:15px;font-weight:600;margin:26px 0 10px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(420px,1fr));gap:14px}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:12px 14px 14px}
.card.fix{border-color:var(--fix)}.card.ok{border-color:var(--ok)}
.t{font-weight:600}.n{color:var(--sub);font-weight:400;margin-right:6px}
.w{color:var(--sub);font-size:12.5px;margin:2px 0 8px}
.img{width:100%;border-radius:10px;display:block;background:#2a2e38;image-rendering:auto}
.row{display:flex;gap:8px;align-items:center;margin-top:10px;flex-wrap:wrap}
button,.seg label{font:inherit;font-size:12.5px;border:1px solid var(--line);background:transparent;color:var(--text);border-radius:8px;padding:4px 10px;cursor:pointer}
.seg{display:flex;gap:6px}.seg input{display:none}
.seg input:checked+label.ok{background:var(--ok);border-color:var(--ok);color:#fff}
.seg input:checked+label.fx{background:var(--fix);border-color:var(--fix);color:#fff}
input.note{flex:1;min-width:160px;font:inherit;font-size:12.5px;padding:5px 9px;border-radius:8px;border:1px solid var(--line);background:transparent;color:var(--text)}
.ctl{display:flex;gap:10px;align-items:center;color:var(--sub);font-size:12.5px}
textarea{width:100%;min-height:120px;font:12.5px Consolas,monospace;border-radius:10px;border:1px solid var(--line);background:var(--card);color:var(--text);padding:10px;box-sizing:border-box}
.nf{background:var(--card);border:1px dashed var(--line);border-radius:14px;padding:12px 14px}
.filt input:checked+label{background:var(--acc);border-color:var(--acc);color:#fff}
.hide{display:none!important}
.empty{color:var(--sub);padding:30px 0;text-align:center}
</style></head><body>
<header><h1>PeakPill · alle Animationen</h1>
<span class="ctl">Tempo <input type="range" id="speed" min="0.25" max="1" step="0.25" value="1"> <span id="sv">1×</span></span>
<span class="ctl" id="count"></span>
<span class="seg filt"><input type="radio" name="f" id="fAll" value="all" checked><label for="fAll">Alle</label><input type="radio" name="f" id="fMine" value="mine"><label for="fMine">Nur zum Überarbeiten</label></span>
<button id="give" style="background:var(--acc);border-color:var(--acc);color:#fff">An Claude übergeben</button>
<button id="copy">Auswahl kopieren</button></header>
<main><div id="out"></div>
<h2>Nicht filmbar (eigenes Fenster bzw. Uhrzeit-abhängig)</h2><div class="grid" id="nf"></div>
<h2>Deine Auswahl</h2><p class="w">Wird beim Ankreuzen automatisch gefüllt – kopieren und mir in den Chat schicken.</p><textarea id="sel" readonly></textarea></main>
<script>
""");
        sb.Append("const C=").Append(json).Append(";\nconst NF=").Append(notFilmed).Append(";\n");
        sb.Append("const STAMP=\"").Append(stamp ?? DateTime.Now.ToString("dd.MM.yyyy HH:mm")).Append("\";\n");
        sb.Append("""
document.querySelector('h1').insertAdjacentHTML('afterend',`<span class="ctl">gefilmt am ${STAMP}</span>`);
// what you marked and wrote stays in this browser (also after closing the page)
const KEY='peakpill-galerie';let saved={};try{saved=JSON.parse(localStorage.getItem(KEY)||'{}')}catch(e){}
function save(){try{localStorage.setItem(KEY,JSON.stringify(saved))}catch(e){}}
const out=document.getElementById('out');let speed=1;
const groups=[...new Set(C.map(c=>c.group))];
for(const g of groups){const h=document.createElement('h2');h.textContent=g;h.dataset.g=g;out.appendChild(h);const grid=document.createElement('div');grid.className='grid';grid.dataset.g=g;out.appendChild(grid);
 for(const c of C.filter(x=>x.group===g)){const d=document.createElement('div');d.className='card';d.id='c'+c.n;d.dataset.id=c.id;
  d.innerHTML=`<div class="t"><span class="n">#${c.n}</span>${c.title}</div><div class="w">${c.what}</div><img class="img">
  <div class="row"><div class="seg"><input type="radio" name="r${c.n}" id="ok${c.n}" value="ok"><label class="ok" for="ok${c.n}">Gefällt</label>
  <input type="radio" name="r${c.n}" id="fx${c.n}" value="fix"><label class="fx" for="fx${c.n}">Überarbeiten</label></div>
  <input class="note" id="t${c.n}" placeholder="Was soll anders werden?"><button>↻</button></div>`;
  grid.appendChild(d);
  const s=saved[c.id]||{};
  if(s.pick){d.querySelector(`input[value=${s.pick}]`).checked=true;d.className='card '+(s.pick==='fix'?'fix':'ok')}
  if(s.note)d.querySelector('.note').value=s.note;
  const img=d.querySelector('img');let i=0,last=0,hold=0;
  function step(ts){if(!d.classList.contains('hide')){const dur=c.ms/speed;if(ts-last>=dur+(hold?600:0)){hold=0;img.src='data:image/jpeg;base64,'+c.frames[i];i++;if(i>=c.frames.length){i=0;hold=1}last=ts}}requestAnimationFrame(step)}
  requestAnimationFrame(step);
  d.querySelector('button').onclick=()=>{i=0};
  d.querySelectorAll('input[type=radio]').forEach(r=>r.onchange=()=>{saved[c.id]={...(saved[c.id]||{}),pick:r.value};save();d.className='card '+(r.value==='fix'?'fix':'ok');upd()});
  d.querySelector('.note').oninput=e=>{saved[c.id]={...(saved[c.id]||{}),note:e.target.value};save();upd()};
 }}
const nf=document.getElementById('nf');for(const x of NF){const d=document.createElement('div');d.className='nf';d.innerHTML=`<div class="t">${x.title}</div><div class="w">${x.what}</div>`;nf.appendChild(d)}
const emptyMsg=document.createElement('div');emptyMsg.className='empty hide';emptyMsg.textContent='Noch nichts zum Überarbeiten markiert – bei „Alle“ eine Animation auf „Überarbeiten“ setzen.';out.prepend(emptyMsg);
// "only mine": everything to rework, and what you liked but wrote something about
function mine(c){const s=saved[c.id]||{};return s.pick==='fix'}
function filter(){const only=document.getElementById('fMine').checked;let any=0;
 for(const c of C){const show=!only||mine(c);document.getElementById('c'+c.n).classList.toggle('hide',!show);if(show)any++}
 document.querySelectorAll('#out h2,#out .grid').forEach(el=>{const g=el.dataset.g;const vis=C.some(c=>c.group===g&&!document.getElementById('c'+c.n).classList.contains('hide'));el.classList.toggle('hide',!vis)});
 emptyMsg.classList.toggle('hide',!(only&&any===0));
 document.querySelectorAll('main>h2,#nf').forEach(el=>{if(el.id==='nf'||el.textContent.startsWith('Nicht filmbar'))el.classList.toggle('hide',only)})}
document.querySelectorAll('input[name=f]').forEach(r=>r.onchange=filter);
function upd(){const ok=[],fx=[];for(const c of C){const s=saved[c.id]||{};const t=(s.note||'').trim();if(s.pick==='fix')fx.push(`#${c.n} ${c.title}${t?' – '+t:''}`);else if(s.pick==='ok')ok.push(`#${c.n} ${c.title}${t?' – '+t:''}`)}
 document.getElementById('sel').value=(fx.length?'Überarbeiten:\n'+fx.join('\n')+'\n\n':'')+(ok.length?'Gefällt:\n'+ok.join('\n'):'');
 document.getElementById('count').textContent=`${fx.length} überarbeiten · ${ok.length} gefällt · ${C.length} gesamt`;
 if(document.getElementById('fMine').checked)filter()}
document.getElementById('speed').oninput=e=>{speed=+e.target.value;document.getElementById('sv').textContent=speed+'×'};
document.getElementById('copy').onclick=()=>{const t=document.getElementById('sel');t.select();try{navigator.clipboard.writeText(t.value)}catch(e){document.execCommand('copy')}};
// hand it over: the list lands as a file in Downloads ("peakpill-auswahl.txt") – Claude reads it from there
document.getElementById('give').onclick=()=>{const t=document.getElementById('sel').value||'(nichts markiert)';
 const a=document.createElement('a');a.href=URL.createObjectURL(new Blob([t],{type:'text/plain;charset=utf-8'}));a.download='peakpill-auswahl.txt';document.body.appendChild(a);a.click();a.remove();
 const b=document.getElementById('give');b.textContent='Übergeben ✓ – sag Claude Bescheid';setTimeout(()=>b.textContent='An Claude übergeben',4000)};
upd();
</script></body></html>
""");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
    }
}
#endif
