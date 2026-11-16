using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PeakPill.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object> _exec;
    private readonly Func<object, bool> _can;

    public RelayCommand(Action<object> exec, Func<object, bool> can = null) { _exec = exec; _can = can; }
    public RelayCommand(Action exec) : this(_ => exec()) { }

    public event EventHandler CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object p) => _can?.Invoke(p) ?? true;
    public void Execute(object p) => _exec(p);
}

public static class Ui
{
    public static Dispatcher D => Application.Current.Dispatcher;

    public static void Run(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }
}

public static class Storage
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeakPill");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Where the app kept its data while it was called IslandHub (renamed to PeakPill 2026-11-16).</summary>
    public static readonly string OldDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandHub");

    static Storage()
    {
        MoveOldData();
        Directory.CreateDirectory(Dir);
    }

    /// <summary>First start after the rename: settings, histories and sign-ins move along (the sign-ins are bound to
    /// the Windows user, not to the folder). Saved file paths inside the lists are pointed at the new folder.</summary>
    private static void MoveOldData()
    {
        try
        {
            if (Directory.Exists(Dir) || !Directory.Exists(OldDir)) return;
            try { Directory.Move(OldDir, Dir); }
            catch
            {
                // something still holds a file – copy instead (the old folder stays as it was)
                foreach (var f in Directory.GetFiles(OldDir, "*", SearchOption.AllDirectories))
                {
                    var to = Path.Combine(Dir, Path.GetRelativePath(OldDir, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(f, to, false);
                }
            }
            string oldJson = JsonEncodedText.Encode(OldDir).ToString(), newJson = JsonEncodedText.Encode(Dir).ToString();
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
            {
                var text = File.ReadAllText(f);
                if (text.Contains(oldJson, StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(f, text.Replace(oldJson, newJson, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { /* the app starts fresh rather than not at all */ }
    }

    public static string PathOf(string name) => Path.Combine(Dir, name);

    public static T Load<T>(string name) where T : new()
    {
        try
        {
            var p = PathOf(name);
            if (File.Exists(p))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(p), Opts) ?? new T();
        }
        catch (Exception ex) { App.Log(ex); }
        return new T();
    }

    public static void Save<T>(string name, T obj)
    {
        try
        {
            var p = PathOf(name);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(obj, Opts));
            File.Move(tmp, p, true);
        }
        catch (Exception ex) { App.Log(ex); }
    }
}

/// <summary>Calls an action at most once per interval (trailing edge). Used to batch disk writes.</summary>
public sealed class Debouncer
{
    private readonly DispatcherTimer _t;
    private Action _a;

    public Debouncer(int ms)
    {
        _t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _t.Tick += (_, _) => { _t.Stop(); _a?.Invoke(); };
    }

    public void Run(Action a) { _a = a; _t.Stop(); _t.Start(); }

    public void Flush() { if (_t.IsEnabled) { _t.Stop(); _a?.Invoke(); } }
}
