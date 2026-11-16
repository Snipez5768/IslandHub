using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>Small dark sheet below the island: a web address (and an optional name) for a new shortcut tile.</summary>
public sealed class LinkPrompt : Window
{
    private readonly TextBox _url = new(), _name = new();
    private readonly TextBlock _error = new() { FontSize = 11.5, Margin = new Thickness(2, 6, 0, 0), Visibility = Visibility.Collapsed };
    public Uri Url { get; private set; }
    public string LinkName => _name.Text;

    public LinkPrompt(string prefill)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = (FontFamily)Application.Current.FindResource("TextFont");

        _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        foreach (var t in new[] { _url, _name })
        {
            t.SetResourceReference(StyleProperty, "MacTextBox");
            t.Width = 300;
            t.Margin = new Thickness(0, 4, 0, 0);
        }
        _url.Tag = "example.com";
        _name.Tag = "Name (optional)";
        _url.Text = prefill ?? "";
        _url.TextChanged += (_, _) => _error.Visibility = Visibility.Collapsed;

        var add = new Button { Content = "Hinzufügen", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        add.SetResourceReference(StyleProperty, "MacAccentButton");
        add.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, Padding = new Thickness(12, 3, 12, 3) };
        cancel.SetResourceReference(StyleProperty, "MacButton");
        cancel.Click += (_, _) => { DialogResult = false; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);

        var title = new TextBlock { Text = "Link hinzufügen", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(2, 0, 0, 8) };
        var body = new StackPanel();
        body.Children.Add(title);
        body.Children.Add(_url);
        body.Children.Add(_name);
        body.Children.Add(_error);
        body.Children.Add(buttons);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), CornerRadius = new CornerRadius(18),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.5),
            Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(12), Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.5 }
        };
        Loaded += (_, _) =>
        {
            // just below the island, centred
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 150;
            _url.Focus();
            _url.SelectAll();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }

    private void Accept()
    {
        Url = LauncherService.ParseLink(_url.Text);
        if (Url == null)
        {
            _error.Text = "Das ist keine Web-Adresse (z. B. example.com).";
            _error.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
