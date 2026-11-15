using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using IslandHub.Core;

namespace IslandHub.UI;

public sealed record WidgetDef(string Kind, string Title, string Glyph, string TemplateKey, Func<object> Content);

public static class WidgetRegistry
{
    public static readonly WidgetDef[] All =
    {
        new("clock",   "Uhr",         "", "W_Clock",   () => App.Hub.Clock),
        new("world",   "Weltzeit",    "", "W_World",   () => App.Hub.Clock),
        new("weather", "Wetter",      "", "W_Weather", () => App.Hub.Weather),
        new("stats",   "PC-Werte",    "", "W_Stats",   () => App.Hub.Stats),
        new("battery", "Akku",        "", "W_Battery", () => App.Hub.Stats),
        new("timer",   "Fokus-Timer", "", "W_Timer",   () => App.Hub.Timer),
        new("todo",    "To-do",       "", "W_Todo",    () => App.Hub.Todos),
    };

    public static WidgetDef Get(string kind) => All.FirstOrDefault(w => w.Kind == kind);

    /// <summary>Builds an iOS "small" widget (150 × 150, continuous corners). The pin button only appears on hover.</summary>
    public static FrameworkElement BuildCard(WidgetDef d, bool desktop, Action pinAction)
    {
        // inside the flat island the cards use compact templates ("C_…"), on the desktop the full ones ("W_…")
        var card = new Border
        {
            Width = desktop ? 158 : 128,
            Height = desktop ? 158 : 81,
            CornerRadius = new CornerRadius(desktop ? 24 : 18),
            Padding = new Thickness(desktop ? 14 : 10),
            Margin = desktop ? new Thickness(12) : new Thickness(0, 0, 8, 0),
            ToolTip = d.Title
        };
        // Liquid Glass card: translucent fill + specular rim
        card.SetResourceReference(Border.BorderBrushProperty, "GlassRim");
        card.BorderThickness = new Thickness(0.8);
        if (desktop)
        {
            card.Background = new LinearGradientBrush(Color.FromArgb(0xE6, 0x2C, 0x2C, 0x30), Color.FromArgb(0xEB, 0x16, 0x16, 0x1A), 90);
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Direction = 270 };
        }
        else card.SetResourceReference(Border.BackgroundProperty, "GlassFill");

        var grid = new Grid();
        var content = new ContentControl
        {
            Content = d.Content(),
            ContentTemplate = (DataTemplate)Application.Current.FindResource(desktop ? d.TemplateKey : "C_" + d.TemplateKey[2..]),
            Foreground = Brushes.White
        };
        content.SetResourceReference(TextElement.FontFamilyProperty, "TextFont");
        grid.Children.Add(content);

        var pin = new Button
        {
            ToolTip = desktop ? "Vom Desktop lösen" : "An den Desktop heften",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -8, -8, 0),
            Opacity = 0
        };
        pin.SetResourceReference(FrameworkElement.StyleProperty, "SmallIconButton");
        Ext.SetIcon(pin, desktop ? "xmark" : "pin");
        Ext.SetHover(pin, (Brush)Application.Current.FindResource(desktop ? "Danger" : "Orange"));
        pin.Click += (_, _) => pinAction?.Invoke();
        grid.Children.Add(pin);
        card.MouseEnter += (_, _) => pin.Opacity = 1;
        card.MouseLeave += (_, _) => pin.Opacity = 0;

        card.Child = grid;
        return card;
    }
}

/// <summary>A widget pinned to the desktop: borderless, stays behind normal windows, draggable.</summary>
public sealed class DesktopWidgetWindow : Window
{
    private static readonly List<DesktopWidgetWindow> Open = new();
    public string Kind { get; }

    private DesktopWidgetWindow(WidgetDef def, double left, double top)
    {
        Kind = def.Kind;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Foreground = Brushes.White;
        SetResourceReference(FontFamilyProperty, "TextFont");
        FontSize = 13;
        Title = "IslandHub – " + def.Title;
        Content = WidgetRegistry.BuildCard(def, true, Unpin);

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox) return;
            try { DragMove(); } catch { }
            SaveAll();
        };
        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
        Activated += (_, _) => SendToBack();
        Loaded += (_, _) => SendToBack();
    }

    private void SendToBack()
    {
        var h = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(h, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void Unpin()
    {
        Open.Remove(this);
        Close();
        SaveAll();
    }

    public static bool IsPinned(string kind) => Open.Any(w => w.Kind == kind);

    public static void Unpin(string kind)
    {
        foreach (var w in Open.Where(w => w.Kind == kind).ToList()) w.Unpin();
    }

    public static void Pin(string kind)
    {
        var def = WidgetRegistry.Get(kind);
        if (def == null) return;
        var existing = Open.FirstOrDefault(w => w.Kind == kind);
        if (existing != null) { existing.Activate(); return; }
        var area = SystemParameters.WorkArea;
        double left = area.Right - 260 - Open.Count * 24, top = area.Top + 120 + Open.Count * 24;
        var w = new DesktopWidgetWindow(def, left, top);
        Open.Add(w);
        w.Show();
        SaveAll();
    }

    public static void RestoreAll()
    {
        var area = SystemParameters.VirtualScreenLeft;
        foreach (var s in App.Settings.DesktopWidgets.ToList())
        {
            var def = WidgetRegistry.Get(s.Kind);
            if (def == null || Open.Any(o => o.Kind == s.Kind)) continue;
            double left = Math.Clamp(s.Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100);
            double top = Math.Clamp(s.Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100);
            var w = new DesktopWidgetWindow(def, left, top);
            Open.Add(w);
            w.Show();
        }
        if (Open.Any(o => o.Kind == "stats")) App.Hub.Stats.WantGpu = true;
    }

    private static void SaveAll()
    {
        App.Settings.DesktopWidgets = Open.Select(o => new DesktopWidgetState { Kind = o.Kind, Left = o.Left, Top = o.Top }).ToList();
        App.SaveSettings();
    }
}
