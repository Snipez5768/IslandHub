using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PeakPill.Core;
using WF = System.Windows.Forms;

namespace PeakPill.UI;

/// <summary>Attached property carrying an icon glyph for templated buttons.</summary>
public static class Ext
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.RegisterAttached("Glyph", typeof(string), typeof(Ext), new FrameworkPropertyMetadata(""));

    public static string GetGlyph(DependencyObject d) => (string)d.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject d, string v) => d.SetValue(GlyphProperty, v);

    /// <summary>Icon name from <see cref="Icons"/> shown by glass button templates.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Ext), new FrameworkPropertyMetadata(null));
    public static string GetIcon(DependencyObject d) => (string)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, string v) => d.SetValue(IconProperty, v);

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.RegisterAttached("IconSize", typeof(double), typeof(Ext), new FrameworkPropertyMetadata(16.0));
    public static double GetIconSize(DependencyObject d) => (double)d.GetValue(IconSizeProperty);
    public static void SetIconSize(DependencyObject d, double v) => d.SetValue(IconSizeProperty, v);

    /// <summary>
    /// Lets a slider without a visible knob be dragged from anywhere on its track:
    /// press → jump there, move → follow the mouse, release → done (mouse capture marks the drag).
    /// </summary>
    public static readonly DependencyProperty DragAnywhereProperty =
        DependencyProperty.RegisterAttached("DragAnywhere", typeof(bool), typeof(Ext), new FrameworkPropertyMetadata(false, OnDragAnywhereChanged));
    public static bool GetDragAnywhere(DependencyObject d) => (bool)d.GetValue(DragAnywhereProperty);
    public static void SetDragAnywhere(DependencyObject d, bool v) => d.SetValue(DragAnywhereProperty, v);

    /// <summary>Raised when a drag on a DragAnywhere slider starts / ends (e.g. the music scrubber seeks on release).</summary>
    public static event Action<System.Windows.Controls.Slider> SliderDragStarted, SliderDragEnded;

    /// <summary>True while any DragAnywhere slider is being dragged.</summary>
    public static bool IsDraggingSlider { get; private set; }

    private static void OnDragAnywhereChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Slider s || e.NewValue is not true) return;
        // handledEventsToo: the Slider's own class handler (IsMoveToPointEnabled) already marks the press handled.
        s.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler((_, a) =>
        {
            if (IsDraggingSlider || !SetFromScreen(s)) return;
            a.Handled = true;
            // The island window does not reliably get Windows mouse capture (it is a non-activating tool
            // window), so the drag follows the real cursor position until the button is released.
            IsDraggingSlider = true;
            SliderDragStarted?.Invoke(s);
            var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
            t.Tick += (_, _) =>
            {
                SetFromScreen(s);
                if ((PeakPill.Core.Native.GetAsyncKeyState(0x01) & 0x8000) != 0) return; // left button still down
                t.Stop();
                IsDraggingSlider = false;
                SliderDragEnded?.Invoke(s);
            };
            t.Start();
        }), true);
    }

    private static bool SetFromScreen(System.Windows.Controls.Slider s)
    {
        if (s.Template?.FindName("PART_Track", s) is not System.Windows.Controls.Primitives.Track track || !track.IsVisible) return false;
        PeakPill.Core.Native.GetCursorPos(out var p);
        var v = track.ValueFromPoint(track.PointFromScreen(new Point(p.X, p.Y)));
        if (double.IsNaN(v)) return false;
        s.Value = Math.Clamp(v, s.Minimum, s.Maximum);
        return true;
    }

    /// <summary>Colour the icon and glass tint take on hover (e.g. red for delete).</summary>
    public static readonly DependencyProperty HoverProperty =
        DependencyProperty.RegisterAttached("Hover", typeof(System.Windows.Media.Brush), typeof(Ext),
            new FrameworkPropertyMetadata(System.Windows.Media.Brushes.White));
    public static System.Windows.Media.Brush GetHover(DependencyObject d) => (System.Windows.Media.Brush)d.GetValue(HoverProperty);
    public static void SetHover(DependencyObject d, System.Windows.Media.Brush v) => d.SetValue(HoverProperty, v);

    /// <summary>Activity mark on a tab (null = none): a small dot, or a little capsule when <see cref="BadgeTextProperty"/> is set.</summary>
    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.RegisterAttached("Badge", typeof(System.Windows.Media.Brush), typeof(Ext), new FrameworkPropertyMetadata(null));
    public static System.Windows.Media.Brush GetBadge(DependencyObject d) => (System.Windows.Media.Brush)d.GetValue(BadgeProperty);
    public static void SetBadge(DependencyObject d, System.Windows.Media.Brush v) => d.SetValue(BadgeProperty, v);

    public static readonly DependencyProperty BadgeTextProperty =
        DependencyProperty.RegisterAttached("BadgeText", typeof(string), typeof(Ext), new FrameworkPropertyMetadata(""));
    public static string GetBadgeText(DependencyObject d) => (string)d.GetValue(BadgeTextProperty);
    public static void SetBadgeText(DependencyObject d, string v) => d.SetValue(BadgeTextProperty, v);
}

public sealed class InverseBoolVisConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullVisConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) =>
        v == null || (v is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// CornerRadius = height / 2. WPF does not clamp large radii like CSS – CornerRadius="99" on a wide button
/// turns into an ellipse. This gives a true capsule at any size.
/// </summary>
public sealed class HalfRadiusConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => new CornerRadius(v is double h && h > 0 ? h / 2 : 0);
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class StrikeConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is true ? TextDecorations.Strikethrough : null;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>A larger island alert (reminders, timers, warnings). Small status messages use IslandWindow.ShowHint instead.</summary>
public sealed class PeekInfo
{
    /// <summary>Icon name from <see cref="Icons"/>.</summary>
    public string Icon { get; set; }
    /// <summary>Alerts with the same key replace each other instead of queuing.</summary>
    public string Key { get; set; }
    /// <summary>Shown even while the island is tucked away (reminders, timer end, warnings).</summary>
    public bool Important { get; set; }
    public System.Windows.Media.Brush GlyphBrush { get; set; }
    public System.Windows.Media.ImageSource Image { get; set; }
    public string Title { get; set; }
    public string Text { get; set; }
    public int TimeoutMs { get; set; } = 3500;
    public List<(string Label, Action Act, bool Accent)> Actions { get; } = new();

    public PeekInfo Action(string label, Action act, bool accent = false)
    {
        Actions.Add((label, act, accent));
        return this;
    }

    // ---------- banner look (approved 2026-11-14): small source line on top, a ring / avatar / cover on the left ----------

    /// <summary>Small grey line above the title ("Claude Code · dynamic island", "Discord · jetzt", "Datenschutz").</summary>
    public string Source { get; set; }
    public string SourceIcon { get; set; }
    public System.Windows.Media.Brush SourceBrush { get; set; }
    /// <summary>0…1: the left symbol sits inside a progress ring in <see cref="GlyphBrush"/> (Claude, downloads).</summary>
    public double? Ring { get; set; }
    /// <summary>A person: a coloured circle with this letter, the app's icon (<see cref="Image"/>) as a small badge.</summary>
    public string Avatar { get; set; }
    public System.Windows.Media.Brush AvatarBrush { get; set; }
    /// <summary>The picture is a cover (rounded square, filled) instead of an app icon on a round plate.</summary>
    public bool Cover { get; set; }
    /// <summary>A screenshot: the island opens downwards, shows it big, then it shrinks into the island.</summary>
    public System.Windows.Media.ImageSource Shot { get; set; }
    /// <summary>
    /// Messages (user wish 2026-11-15): first a one-line ticker in the closed island (it only grows sideways);
    /// the pointer on it turns it into the full card (<see cref="Open"/>).
    /// </summary>
    public bool Ticker { get; set; }
    public bool Open { get; set; }
    /// <summary>How many messages this banner stands for (newer ones replace it and count up).</summary>
    public int Count { get; set; } = 1;

    /// <summary>A click on the banner does this (e.g. opens the chat); without it a click just dismisses.</summary>
    public Action Click { get; set; }
    /// <summary>Round symbol buttons on the right: icon, tooltip, fill (null = glass), dark glyph on a light fill.</summary>
    public List<(string Icon, string Tip, System.Windows.Media.Brush Fill, bool DarkGlyph, Action Act)> Buttons { get; } = new();

    /// <summary>Messengers: the card gets "Antworten" – the text typed in the island is handed to this (2026-11-16).</summary>
    public Action<string> Reply { get; set; }

    /// <summary>A moment card (2026-11-18: focus done, good morning, evening): rows that come in one by one.</summary>
    public List<(string Glyph, System.Windows.Media.Brush Color, string Text, string Sub)> Rows { get; } = new();
    /// <summary>Small grey text on the right of the card's title (a date, a time).</summary>
    public string Meta { get; set; }
    /// <summary>A picture in the card (a QR code to scan).</summary>
    public System.Windows.Media.ImageSource Picture { get; set; }

    public PeekInfo Button(string icon, string tip, Action act, System.Windows.Media.Brush fill = null, bool darkGlyph = false)
    {
        Buttons.Add((icon, tip, fill, darkGlyph, act));
        return this;
    }
}

public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ToolStripMenuItem _queueItem, _hideItem;

    public TrayIcon()
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add(L.T("Insel öffnen"), null, (_, _) => App.Island.Expand(null, true));
        menu.Items.Add(L.T("Zwischenablage"), null, (_, _) => App.Island.Expand("Clip", true));
        menu.Items.Add("Screenshot", null, (_, _) => App.Island.TakeScreenshot(false));
        _queueItem = new WF.ToolStripMenuItem(L.T("Stapel-Modus"), null, (_, _) => App.Hub.Clipboard.QueueMode = !App.Hub.Clipboard.QueueMode);
        menu.Items.Add(_queueItem);
        _hideItem = new WF.ToolStripMenuItem(L.T("Insel ausblenden"), null, (_, _) => App.Island.ToggleHidden());
        menu.Items.Add(_hideItem);
        menu.Items.Add(L.T("Insel verschieben"), null, (_, _) => App.Island.EnterMoveMode());
        var arrange = new WF.ToolStripMenuItem(L.T("Widgets anordnen"), null, (_, _) => DesktopWidgetWindow.SetArranging(!DesktopWidgetWindow.Arranging));
        menu.Items.Add(arrange);
        menu.Opening += (_, _) => arrange.Text = DesktopWidgetWindow.Arranging ? L.T("Widgets fixieren") : L.T("Widgets anordnen");
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(L.T("Einstellungen"), null, (_, _) => SettingsWindow.ShowSingle());
        menu.Items.Add(L.T("Beenden"), null, (_, _) => App.Quit());
        menu.Opening += (_, _) =>
        {
            _queueItem.Checked = App.Hub.Clipboard.QueueMode;
            _hideItem.Text = App.Island.IsHiddenByUser ? L.T("Insel einblenden") : L.T("Insel ausblenden");
        };

        _icon = new WF.NotifyIcon
        {
            Icon = IconFactory.CreateTrayIcon(),
            Text = "PeakPill",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) App.Island.Expand(null, true); };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) SettingsWindow.ShowSingle(); };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
