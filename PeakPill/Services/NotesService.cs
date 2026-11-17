using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>One quick note – free text, optionally pinned to the top.</summary>
public sealed class Note : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Created { get; set; } = DateTime.Now;

    private string _text = "";
    public string Text { get => _text; set { if (Set(ref _text, value ?? "")) Raise(nameof(Preview)); } }

    private bool _pinned;
    public bool Pinned { get => _pinned; set => Set(ref _pinned, value); }

    /// <summary>Up to three lines for the list.</summary>
    [JsonIgnore] public string Preview => Text.Length > 400 ? Text[..400] + "…" : Text;

    /// <summary>"14:32", "gestern", "Mo., 12.11." …</summary>
    [JsonIgnore]
    public string When
    {
        get
        {
            var d = Created;
            if (d.Date == DateTime.Today) return d.ToString("HH:mm");
            if (d.Date == DateTime.Today.AddDays(-1)) return L.T("gestern");
            return d.ToString(d.Year == DateTime.Today.Year ? "d. MMM" : "d. MMM yyyy", L.Culture);
        }
    }
}

/// <summary>
/// Quick notes in the island (user wish 2026-11-17): a shortcut opens the field, Enter keeps the note.
/// Pinned ones stay on top, the rest newest first. Stored in %AppData%\PeakPill\notes.json – nothing leaves the PC.
/// </summary>
public sealed class NotesService
{
    private const string FileName = "notes.json";
    public ObservableCollection<Note> Items { get; } = new();

    public void Load()
    {
        foreach (var n in Storage.Load<List<Note>>(FileName) ?? new()) Items.Add(n);
        Sort();
    }

    public void Save() => Storage.Save(FileName, Items.ToList());

    public Note Add(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return null;
        var n = new Note { Text = text };
        Items.Add(n);
        Sort();
        Save();
        return n;
    }

    public void Remove(Note n)
    {
        if (n == null) return;
        Items.Remove(n);
        Save();
    }

    /// <summary>"Rückgängig" after deleting.</summary>
    public void Restore(Note n, int at)
    {
        if (n == null || Items.Contains(n)) return;
        Items.Insert(Math.Clamp(at, 0, Items.Count), n);
        Save();
    }

    public void TogglePin(Note n)
    {
        if (n == null) return;
        n.Pinned = !n.Pinned;
        Sort();
        Save();
    }

    public void Update(Note n, string text)
    {
        if (n == null) return;
        text = (text ?? "").Trim();
        if (text.Length == 0) { Remove(n); return; }
        n.Text = text;
        Save();
    }

    /// <summary>Pinned first, then newest first – moved in place so the list doesn't flicker.</summary>
    private void Sort()
    {
        var order = Items.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Created).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            int at = Items.IndexOf(order[i]);
            if (at != i) Items.Move(at, i);
        }
    }
}
