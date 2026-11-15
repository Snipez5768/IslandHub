using System.Windows;
using IslandHub.Core;
using IslandHub.Services;
using IslandHub.UI;

namespace IslandHub;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; }
    public static Hub Hub { get; private set; }
    public static IslandWindow Island { get; private set; }
    public static event Action SettingsChanged;

    private Mutex _mutex;
    private TrayIcon _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\IslandHub.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("IslandHub läuft bereits (siehe Infobereich der Taskleiste).", "IslandHub");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception); a.SetObserved(); };

        Settings = Storage.Load<AppSettings>("settings.json");
        Settings.EnsureDefaults();
        if (Settings.Autostart) Autostart.Refresh();
        SaveSettings();
        ThemeManager.Apply();

        Hub = new Hub();
        Hub.Init();

        Island = new IslandWindow();
        Island.Show();
        Hub.Hotkeys.Pressed += Island.OnHotkey;

        _tray = new TrayIcon();
        var failed = ApplyHotkeys();
        if (failed.Count > 0)
            Island.ShowPeek(new PeekInfo
            {
                Icon = "keyboard",
                Important = true,
                Title = "Kürzel belegt",
                Text = string.Join(", ", failed.Select(a => HotkeyInfo.All.First(h => h.Action == a).Label)),
                TimeoutMs = 6000
            }.Action("Ändern", () => SettingsWindow.ShowSingle("Keys"), true));

        DesktopWidgetWindow.RestoreAll();

        int pt = Array.IndexOf(e.Args, "--privacy-test");
        if (pt >= 0 && pt + 1 < e.Args.Length)
        {
            // logs what the Live detection sees for a few seconds, then quits
            var file = e.Args[pt + 1];
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 5; i++)
                {
                    await Task.Delay(1500);
                    var line = Current.Dispatcher.Invoke(() => string.Join(" | ", Hub.Privacy.Apps.Select(a => $"{a.Name} [{a.Parts}] {a.Elapsed}")
                        .Concat(Hub.Agents.Sessions.Select(s => $"Agent {s.Project}: {s.State} '{s.Task}' step='{s.Step}' {s.Counter} {s.Elapsed}"))));
                    File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss} {(line.Length == 0 ? "(nichts)" : line)}\n");
                }
                Current.Dispatcher.Invoke(Quit);
            });
            return;
        }

        int nt = Array.IndexOf(e.Args, "--notify-test");
        if (nt >= 0 && nt + 1 < e.Args.Length)
        {
            // reads notifications + tries every kind of price alarm (separate list, nothing saved), then quits
            var file = e.Args[nt + 1];
            Current.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var sb = new System.Text.StringBuilder();
                await Task.Delay(3000);
                sb.AppendLine($"notifications: {Hub.Notifications.Items.Count}");
                foreach (var n in Hub.Notifications.Items.Take(6))
                    sb.AppendLine($"  [{n.AppName}] icon={(n.Icon != null)} launch={(n.Launch != null)} | {n.Header} | {(n.Text.Length > 40 ? n.Text[..40] : n.Text)} | {n.Ago}");
                var test = new PriceAlertService { Persist = false };
                foreach (var (input, rule) in new[] { ("BTC", ""), ("AAPL", ""), ("Silksong", ""), ("epic", ""), ("twitch:papaplatte", ""), ("https://www.amazon.de/dp/B0F2TT8Q7M", "<150") })
                {
                    var (a, err) = await test.AddAsync(input, rule);
                    sb.AppendLine(a == null ? $"{input}: FEHLER {err}"
                        : $"{input}: {a.Kind} '{a.Name}' {a.PriceText} {a.ChangeText} | {a.RuleText} | {a.Status} err={a.Error}");
                }
                File.WriteAllText(file, sb.ToString());
                Quit();
            }));
            return;
        }

        int stt = Array.IndexOf(e.Args, "--steam-test");
        if (stt >= 0 && stt + 1 < e.Args.Length)
        {
            var file = e.Args[stt + 1];
            Current.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 4; i++)
                {
                    await Task.Delay(2500);
                    sb.AppendLine($"{DateTime.Now:HH:mm:ss} downloading={Hub.Steam.Downloading} '{Hub.Steam.Name}' {Hub.Steam.Percent} {Hub.Steam.Speed}");
                }
                File.WriteAllText(file, sb.ToString());
                Quit();
            }));
            return;
        }

        int sp = Array.IndexOf(e.Args, "--spectrum-test");
        if (sp >= 0 && sp + 1 < e.Args.Length)
        {
            var file = e.Args[sp + 1];
            Current.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var a = new SpectrumAnalyzer();
                a.Start();
                var sb = new System.Text.StringBuilder($"running={a.Running}\n");
                await Task.Delay(300);
                for (int i = 0; i < 20; i++) { sb.AppendLine(string.Join(" ", a.Levels().Select(v => v.ToString("0.00")))); await Task.Delay(60); }
                a.Stop();
                File.WriteAllText(file, sb.ToString());
                Quit();
            }));
            return;
        }

        int dt = Array.IndexOf(e.Args, "--discord-test");
        if (dt >= 0 && dt + 1 < e.Args.Length)
        {
            // logs what Discord's RPC reports; with "toggle" it mutes in Discord and unmutes again right after
            var file = e.Args[dt + 1];
            bool toggle = e.Args.Contains("toggle");
            Current.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var sb = new System.Text.StringBuilder();
                string State() => $"connected={Hub.Discord.Connected} status='{Hub.Discord.Status}' inVoice={Hub.Discord.InVoice} channel='{Hub.Discord.Channel}' muted={Hub.Discord.Muted} deaf={Hub.Discord.Deafened}";
                for (int i = 0; i < 8 && !Hub.Discord.Connected; i++) await Task.Delay(1000);
                await Task.Delay(1500);
                sb.AppendLine($"{DateTime.Now:HH:mm:ss} {State()}");
                if (toggle && Hub.Discord.Connected)
                {
                    bool before = Hub.Discord.Muted;
                    sb.AppendLine($"set mute={!before}: {await Hub.Discord.SetMute(!before)}");
                    await Task.Delay(800);
                    sb.AppendLine($"{DateTime.Now:HH:mm:ss} {State()}");
                    sb.AppendLine($"set mute={before}: {await Hub.Discord.SetMute(before)}");
                    await Task.Delay(800);
                    sb.AppendLine($"{DateTime.Now:HH:mm:ss} {State()}");
                }
                File.WriteAllText(file, sb.ToString());
                Quit();
            }));
            return;
        }

        int gt = Array.IndexOf(e.Args, "--games-test");
        if (gt >= 0 && gt + 1 < e.Args.Length) { Island.GamesClickTest(e.Args[gt + 1]); return; }

        int st = Array.IndexOf(e.Args, "--slider-test");
        if (st >= 0 && st + 1 < e.Args.Length) { Island.SliderTest(e.Args[st + 1]); return; }

        int rt = Array.IndexOf(e.Args, "--render-test");
        if (rt >= 0 && rt + 1 < e.Args.Length) { _ = Island.RenderTestAsync(e.Args[rt + 1]); return; }

#if DEBUG
        TestRun = e.Args.Any(a => a.StartsWith("--") && a.EndsWith("-test"));
        int rot = Array.IndexOf(e.Args, "--reorder-test");
        if (rot >= 0 && rot + 1 < e.Args.Length) { _ = ReorderTestAsync(e.Args[rot + 1]); return; }

        int alt = Array.IndexOf(e.Args, "--alarm-test");
        if (alt >= 0 && alt + 1 < e.Args.Length) { _ = Island.AlarmTestAsync(e.Args[alt + 1]); return; }

        int skt = Array.IndexOf(e.Args, "--skip-test");
        if (skt >= 0 && skt + 1 < e.Args.Length) { _ = Island.SkipTestAsync(e.Args[skt + 1]); return; }

        int tkt = Array.IndexOf(e.Args, "--ticker-test");
        if (tkt >= 0 && tkt + 1 < e.Args.Length) { _ = Island.TickerTestAsync(e.Args[tkt + 1]); return; }

        int sgt = Array.IndexOf(e.Args, "--song-test");
        if (sgt >= 0 && sgt + 1 < e.Args.Length) { _ = Island.SongTestAsync(e.Args[sgt + 1]); return; }

        int tour = Array.IndexOf(e.Args, "--settings-tour");
        if (tour >= 0 && tour + 1 < e.Args.Length) { TestRun = true; _ = SettingsWindow.TourAsync(e.Args[tour + 1]); return; }

        int sett = Array.IndexOf(e.Args, "--settings-test");
        if (sett >= 0 && sett + 1 < e.Args.Length) { _ = SettingsWindow.TestAsync(e.Args[sett + 1]); return; }

        int mot = Array.IndexOf(e.Args, "--moment-test");
        if (mot >= 0 && mot + 1 < e.Args.Length) { _ = Island.MomentTestAsync(e.Args[mot + 1]); return; }

        int bnt = Array.IndexOf(e.Args, "--banner-test");
        if (bnt >= 0 && bnt + 1 < e.Args.Length) { _ = Island.BannerTestAsync(e.Args[bnt + 1]); return; }

        int rmt = Array.IndexOf(e.Args, "--room-test");
        if (rmt >= 0 && rmt + 1 < e.Args.Length) { _ = Island.RoomTestAsync(e.Args[rmt + 1]); return; }

        int stt2 = Array.IndexOf(e.Args, "--smarttab-test");
        if (stt2 >= 0 && stt2 + 1 < e.Args.Length) { Island.SmartTabTest(e.Args[stt2 + 1]); return; }

        int qt = Array.IndexOf(e.Args, "--queue-test");
        if (qt >= 0 && qt + 1 < e.Args.Length) { _ = QueueTestAsync(e.Args[qt + 1]); return; }

        int mt = Array.IndexOf(e.Args, "--motion-test");
        if (mt >= 0 && mt + 1 < e.Args.Length) { _ = Island.MotionTestAsync(e.Args[mt + 1]); return; }
#endif
        // no "IslandHub läuft" banner at start any more (user wish 2026-11-15) – the island appearing says it
    }

#if DEBUG
    /// <summary>
    /// Queue mode end to end: copies "Eins", "Zwei", "Drei", then presses Ctrl+V three times into its OWN test window
    /// (it stops if that window is not in front, so nothing is ever pasted into another app). History, settings and
    /// the user's clipboard text are restored afterwards.
    /// </summary>
    private static async Task QueueTestAsync(string file)
    {
        var log = new System.Text.StringBuilder();
        string before = null;
        try { if (Clipboard.ContainsText()) before = Clipboard.GetText(); } catch { }
        bool queueBefore = Settings.QueueMode;
        Hub.Clipboard.Persist = false;
        var box = new System.Windows.Controls.TextBox { FontSize = 16, AcceptsReturn = false };
        var win = new Window { Title = "IslandHub Stapel-Test", Width = 420, Height = 140, Content = box, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        try
        {
            Hub.Clipboard.QueueMode = true;
            Hub.Clipboard.AcceptInjectedPasteForTest();
            win.Show();
            win.Activate();
            box.Focus();
            await Task.Delay(500);
            foreach (var t in new[] { "Eins", "Zwei", "Drei" })
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, t), true);
                await Task.Delay(700);
                log.AppendLine($"kopiert {t}: Stapel={Hub.Clipboard.QueueCount} Ablage jetzt='{SafeText()}'");
            }
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            log.AppendLine($"Tastatur-Hook läuft (eigener Thread): {Hub.Clipboard.HookRunningForTest}");
            bool front = Native.GetForegroundWindow() == hwnd;
            for (int i = 0; i < 3; i++)
            {
                if (front)
                {
                    // real keys into the test window (only when it is really in front)
                    win.Activate(); box.Focus();
                    await Task.Delay(250);
                    keybd_event(0x11, 0, 0, IntPtr.Zero); keybd_event(0x56, 0, 0, IntPtr.Zero);
                    keybd_event(0x56, 0, 2, IntPtr.Zero); keybd_event(0x11, 0, 2, IntPtr.Zero);
                }
                else
                {
                    // Windows did not let the test window come to the front: paste into it directly and report the
                    // paste to the queue exactly as the hook would – no key ever reaches another app
                    box.Paste();
                    Hub.Clipboard.SimulatePasteForTest();
                }
                await Task.Delay(700);
                log.AppendLine($"{(front ? "Strg+V" : "Einfügen (simuliert)")} #{i + 1}: Text='{box.Text}' Stapel={Hub.Clipboard.QueueCount} Ablage jetzt='{SafeText()}'");
            }
            log.AppendLine(box.Text == "EinsZweiDrei" ? "OK" : "FEHLER: erwartet 'EinsZweiDrei'");
        }
        catch (Exception ex) { log.AppendLine("Ausnahme: " + ex.Message); }
        finally
        {
            win.Close();
            Hub.Clipboard.QueueMode = queueBefore;
            try { if (before != null) Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, before), true); } catch { }
            File.WriteAllText(file, log.ToString());
            Quit();
        }

        static string SafeText() { try { return Clipboard.ContainsText() ? Clipboard.GetText() : "(kein Text)"; } catch { return "(belegt)"; } }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    /// <summary>
    /// Drag along one axis in its own top-most window (the pointer moves for ~2 s and is put back): tile 0 is pulled
    /// two places to the right, checked mid-way for the snap, then row 3 of a vertical list is pulled to the top.
    /// </summary>
    private static async Task ReorderTestAsync(string file)
    {
        var log = new System.Text.StringBuilder();
        Native.GetCursorPos(out var home);
        Hub.Clipboard.Persist = false;
        var h = new System.Collections.ObjectModel.ObservableCollection<string>(new[] { "A", "B", "C", "D", "E" });
        var v = new System.Collections.ObjectModel.ObservableCollection<string>(new[] { "1", "2", "3", "4" });
        System.Windows.Controls.ItemsControl Make(System.Collections.IEnumerable src, bool horizontal)
        {
            var ic = new System.Windows.Controls.ItemsControl { ItemsSource = src };
            var panel = new FrameworkElementFactory(typeof(System.Windows.Controls.StackPanel));
            panel.SetValue(System.Windows.Controls.StackPanel.OrientationProperty, horizontal ? System.Windows.Controls.Orientation.Horizontal : System.Windows.Controls.Orientation.Vertical);
            ic.ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(panel);
            var b = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            b.SetValue(FrameworkElement.WidthProperty, horizontal ? 56.0 : 200.0);
            b.SetValue(FrameworkElement.HeightProperty, horizontal ? 50.0 : 34.0);
            b.SetValue(FrameworkElement.MarginProperty, horizontal ? new Thickness(0, 0, 6, 0) : new Thickness(0, 0, 0, 4));
            b.SetValue(System.Windows.Controls.Border.BackgroundProperty, System.Windows.Media.Brushes.SteelBlue);
            var t = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            t.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding());
            b.AppendChild(t);
            ic.ItemTemplate = new DataTemplate { VisualTree = b };
            return ic;
        }
        var hList = Make(h, true);
        var vList = Make(v, false);
        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        root.Children.Add(hList);
        root.Children.Add(new System.Windows.Controls.Border { Height = 20 });
        root.Children.Add(vList);
        var win = new Window { Title = "IslandHub Zieh-Test", Width = 420, Height = 300, Content = root, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowActivated = false };
        double snapOffset = double.NaN;
        var hr = new UI.AxisReorder(hList, true, (f, t) => h.Move(f, t));
        var vr = new UI.AxisReorder(vList, false, (f, t) => v.Move(f, t));
        try
        {
            win.Show();
            await Task.Delay(600);
            Point Screen(System.Windows.Controls.ItemsControl ic, int i, double dx = 0, double dy = 0)
            {
                var c = (FrameworkElement)ic.ItemContainerGenerator.ContainerFromIndex(i);
                return c.PointToScreen(new Point(c.ActualWidth / 2 + dx, c.ActualHeight / 2 + dy));
            }
            async Task Drag(Point from, Point to, Func<Task> midway = null)
            {
                SetCursorPos((int)from.X, (int)from.Y); await Task.Delay(120);
                mouse_event(0x2, 0, 0, 0, IntPtr.Zero); await Task.Delay(80);
                for (int i = 1; i <= 12; i++)
                {
                    SetCursorPos((int)(from.X + (to.X - from.X) * i / 12), (int)(from.Y + (to.Y - from.Y) * i / 12));
                    await Task.Delay(35);
                    if (i == 6 && midway != null) await midway();
                }
                await Task.Delay(150);
                mouse_event(0x4, 0, 0, 0, IntPtr.Zero);
                await Task.Delay(500);
            }
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(win);
            double pitch = 62 * dpi.DpiScaleX;
            // A → after C (two places); half-way (one place + 3 px) it must sit snapped on slot 1
            await Drag(Screen(hList, 0), Screen(hList, 0, 2 * 62), async () =>
            {
                var c0 = (FrameworkElement)hList.ItemContainerGenerator.ContainerFromIndex(0);
                await Task.Delay(200);
                var tt = c0.RenderTransform as System.Windows.Media.TranslateTransform
                         ?? (c0.RenderTransform as System.Windows.Media.TransformGroup)?.Children.OfType<System.Windows.Media.TranslateTransform>().FirstOrDefault();
                var sc = (c0.RenderTransform as System.Windows.Media.TransformGroup)?.Children.OfType<System.Windows.Media.ScaleTransform>().FirstOrDefault();
                snapOffset = tt?.X ?? double.NaN;
                log.AppendLine($"  angehoben: Größe {sc?.ScaleX:0.00} (erwartet 1,08)");
            });
            log.AppendLine($"waagerecht: {string.Join("", h)} (erwartet BCADE) {(string.Join("", h) == "BCADE" ? "OK" : "FEHLER")}");
            log.AppendLine($"  halber Weg: Versatz {snapOffset:0} px (eingerastet wäre 62) {(Math.Abs(snapOffset - 62) < 1 ? "OK eingerastet" : "folgt dem Zeiger")}");
            // vertical: "4" to the top
            await Drag(Screen(vList, 3), Screen(vList, 3, 0, -3 * 38));
            log.AppendLine($"senkrecht: {string.Join("", v)} (erwartet 4123) {(string.Join("", v) == "4123" ? "OK" : "FEHLER")}");
            // a press without moving is still a click (no reorder)
            var before = string.Join("", h);
            var p = Screen(hList, 1);
            SetCursorPos((int)p.X, (int)p.Y); await Task.Delay(100);
            mouse_event(0x2, 0, 0, 0, IntPtr.Zero); await Task.Delay(60); mouse_event(0x4, 0, 0, 0, IntPtr.Zero); await Task.Delay(300);
            log.AppendLine($"Klick ohne Ziehen: {string.Join("", h)} {(string.Join("", h) == before && !hr.JustDragged ? "OK unverändert" : "FEHLER")}");
        }
        catch (Exception ex) { log.AppendLine("Ausnahme: " + ex.Message); }
        finally
        {
            SetCursorPos(home.X, home.Y);
            win.Close();
            File.WriteAllText(file, log.ToString());
            Quit();
        }
    }
#endif

    public static List<HotkeyAction> ApplyHotkeys() => Hub.Hotkeys.ApplyFromSettings(Settings);

    /// <summary>A debug test run: it may change settings while it runs, but never saves them (the user's stay as they are).</summary>
    public static bool TestRun { get; private set; }

    public static void SaveSettings()
    {
        if (TestRun) return;
        Storage.Save("settings.json", Settings);
    }

    public static void NotifySettingsChanged()
    {
        SaveSettings();
        ThemeManager.Apply();
        SettingsChanged?.Invoke();
    }

    public static void Log(Exception ex)
    {
        if (ex == null) return;
        try { File.AppendAllText(Storage.PathOf("log.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n"); } catch { }
    }

    public static void Quit()
    {
        var app = (App)Current;
        app._tray?.Dispose();
        app._tray = null;
        Hub?.Shutdown();
        SaveSettings();
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
