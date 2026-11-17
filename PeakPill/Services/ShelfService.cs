using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Windows.Media;
using PeakPill.Core;

namespace PeakPill.Services;

public sealed class ShelfItem : ObservableObject
{
    private ImageSource _icon;
    public string Path { get; set; }

    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    [JsonIgnore] public bool IsFolder => Directory.Exists(Path);
    [JsonIgnore] public ImageSource Icon => _icon ??= ShellIcon.Get(Path);

    [JsonIgnore]
    public string SizeText
    {
        get
        {
            try
            {
                if (IsFolder) return L.T("Ordner");
                var len = new FileInfo(Path).Length;
                return FormatSize(len);
            }
            catch { return "fehlt"; }
        }
    }

    public static string FormatSize(long b) =>
        b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0.#} KB" : b < 1024L * 1024 * 1024 ? $"{b / 1024.0 / 1024:0.#} MB" : $"{b / 1024.0 / 1024 / 1024:0.##} GB";
}

/// <summary>"Dateiablage": files parked on the island so they can be dragged into any app later.</summary>
public sealed class ShelfService
{
    public ObservableCollection<ShelfItem> Items { get; } = new();

    public void Load()
    {
        foreach (var p in Storage.Load<List<string>>("shelf.json"))
            if (File.Exists(p) || Directory.Exists(p)) Items.Add(new ShelfItem { Path = p });
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p) || Items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            if (File.Exists(p) || Directory.Exists(p)) Items.Insert(0, new ShelfItem { Path = p });
        }
        Save();
    }

    public void Remove(ShelfItem i) { Items.Remove(i); Save(); }
    public void Clear() { Items.Clear(); Save(); }
    public void PruneMissing()
    {
        foreach (var i in Items.Where(i => !File.Exists(i.Path) && !Directory.Exists(i.Path)).ToList()) Items.Remove(i);
        Save();
    }

    public void Save() => Storage.Save("shelf.json", Items.Select(i => i.Path).ToList());
}
