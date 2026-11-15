using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace IslandHub.Core;

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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandHub");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    static Storage() { Directory.CreateDirectory(Dir); }

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
