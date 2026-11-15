using System.Diagnostics;
using System.Management;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;
using IslandHub.Core;
using WF = System.Windows.Forms;

namespace IslandHub.Services;

/// <summary>CPU / RAM / GPU load and battery.</summary>
public sealed class SystemStats : ObservableObject
{
    private double _cpu, _ram, _gpu;
    private bool _hasGpu, _hasBattery, _charging;
    private int _battery;
    private string _ramText = "", _batteryText = "";
    private long _lastIdle, _lastTotal;
    private Timer _timer;
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new();
    private int _tick;

    public double Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public double Ram { get => _ram; private set => Set(ref _ram, value); }
    public double Gpu { get => _gpu; private set => Set(ref _gpu, value); }
    public bool HasGpu { get => _hasGpu; private set => Set(ref _hasGpu, value); }
    public string RamText { get => _ramText; private set => Set(ref _ramText, value); }
    public bool HasBattery { get => _hasBattery; private set => Set(ref _hasBattery, value); }
    public bool Charging { get => _charging; private set => Set(ref _charging, value); }
    public int Battery { get => _battery; private set => Set(ref _battery, value); }
    public string BatteryText { get => _batteryText; private set => Set(ref _batteryText, value); }
    public string CpuText => $"{Cpu:0} %";
    public string GpuText => $"{Gpu:0} %";
    public string RamPercentText => $"{Ram:0} %";
    public string BatteryPercentText => $"{Battery} %";
    public string Summary => HasGpu ? $"CPU {Cpu:0}%   GPU {Gpu:0}%   RAM {Ram:0}%" : $"CPU {Cpu:0}%   RAM {Ram:0}%";

    /// <summary>GPU counters are comparatively expensive – only sample while something shows them.</summary>
    public bool WantGpu { get; set; }

    public void Start() => _timer = new Timer(_ => Sample(), null, 500, 2000);

    private void Sample()
    {
        try
        {
            double cpu = 0;
            if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                long total = kernel + user;
                long dIdle = idle - _lastIdle, dTotal = total - _lastTotal;
                if (_lastTotal != 0 && dTotal > 0) cpu = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
                _lastIdle = idle; _lastTotal = total;
            }

            var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
            Native.GlobalMemoryStatusEx(ref mem);
            double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024.0 / 1024 / 1024;
            double totalGb = mem.ullTotalPhys / 1024.0 / 1024 / 1024;

            double? gpu = WantGpu ? SampleGpu() : null;

            var ps = WF.SystemInformation.PowerStatus;
            bool hasBat = !ps.BatteryChargeStatus.HasFlag(WF.BatteryChargeStatus.NoSystemBattery) && ps.BatteryLifePercent <= 1.0f;
            int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
            bool charging = ps.PowerLineStatus == WF.PowerLineStatus.Online;
            string batText = !hasBat ? "Kein Akku" :
                charging ? (pct >= 100 ? "Voll · Netzbetrieb" : "Wird geladen") :
                ps.BatteryLifeRemaining > 0 ? $"Noch ca. {TimeSpan.FromSeconds(ps.BatteryLifeRemaining):h\\:mm} Std" : "Akkubetrieb";

            Ui.Run(() =>
            {
                Cpu = cpu;
                Ram = mem.dwMemoryLoad;
                RamText = $"{usedGb:0.0} / {totalGb:0} GB";
                if (gpu.HasValue) { Gpu = gpu.Value; HasGpu = true; }
                HasBattery = hasBat; Battery = pct; Charging = charging; BatteryText = batText;
                Raise(nameof(CpuText)); Raise(nameof(GpuText)); Raise(nameof(RamPercentText)); Raise(nameof(BatteryPercentText)); Raise(nameof(Summary));
            });
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private double? SampleGpu()
    {
        try
        {
            if (_tick++ % 10 == 0)
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                var names = cat.GetInstanceNames().Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)).ToHashSet();
                foreach (var gone in _gpuCounters.Keys.Where(k => !names.Contains(k)).ToList())
                {
                    _gpuCounters[gone].Dispose();
                    _gpuCounters.Remove(gone);
                }
                foreach (var n in names.Where(n => !_gpuCounters.ContainsKey(n)))
                {
                    var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true);
                    pc.NextValue();
                    _gpuCounters[n] = pc;
                }
            }
            double sum = 0;
            foreach (var pc in _gpuCounters.Values)
            {
                try { sum += pc.NextValue(); } catch { }
            }
            return Math.Clamp(sum, 0, 100);
        }
        catch { return null; }
    }
}

/// <summary>Speaker volume and microphone mute (all capture devices at once).</summary>
public sealed class AudioService : ObservableObject
{
    private readonly MMDeviceEnumerator _en = new();
    private bool _micMuted;
    private double _volume;
    private bool _muted;
    private bool _updating;

    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, value) || _updating) return;
            try
            {
                using var d = _en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                d.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(value / 100.0, 0, 1);
                if (_muted && value > 0) { d.AudioEndpointVolume.Mute = false; Muted = false; }
            }
            catch { }
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value) || _updating) return;
            try
            {
                using var d = _en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                d.AudioEndpointVolume.Mute = value;
            }
            catch { }
        }
    }

    public bool MicMuted { get => _micMuted; private set => Set(ref _micMuted, value); }

    public void Refresh()
    {
        _updating = true;
        try
        {
            using var d = _en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            Volume = Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            Muted = d.AudioEndpointVolume.Mute;
        }
        catch { }
        finally { _updating = false; }
        RefreshMic();
    }

    public void RefreshMic()
    {
        try
        {
            var list = _en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            MicMuted = list.Count > 0 && list.All(d => d.AudioEndpointVolume.Mute);
            foreach (var d in list) d.Dispose();
        }
        catch { }
    }

    private List<MMDevice> _caps;
    private DateTime _capsAt;

    /// <summary>
    /// How loud the microphone is right now (0…1, the loudest active mic) – read from the device's own level meter,
    /// so IslandHub never opens the microphone itself (no extra "in use" sign). Only moves while an app records.
    /// </summary>
    public float MicLevel()
    {
        try
        {
            if (_caps == null || DateTime.Now - _capsAt > TimeSpan.FromSeconds(5))
            {
                if (_caps != null) foreach (var d in _caps) d.Dispose();
                _caps = _en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
                _capsAt = DateTime.Now;
            }
            float p = 0;
            foreach (var d in _caps)
                try { p = Math.Max(p, d.AudioMeterInformation.MasterPeakValue); } catch { }
            return p;
        }
        catch { return 0; }
    }

    /// <summary>Toggles mute on every active microphone; returns the new state.</summary>
    public bool ToggleMic()
    {
        RefreshMic();
        bool target = !MicMuted;
        try
        {
            foreach (var d in _en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList())
            {
                try { d.AudioEndpointVolume.Mute = target; } catch { }
                d.Dispose();
            }
        }
        catch { }
        MicMuted = target;
        return target;
    }
}

/// <summary>Wi-Fi and Bluetooth radios.</summary>
public sealed class RadioService : ObservableObject
{
    private Radio _wifi, _bt;
    private bool _wifiOn, _btOn;

    public bool HasWifi => _wifi != null;
    public bool HasBluetooth => _bt != null;
    public bool WifiOn { get => _wifiOn; private set => Set(ref _wifiOn, value); }
    public bool BluetoothOn { get => _btOn; private set => Set(ref _btOn, value); }

    public async Task InitAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return;
            var radios = await Radio.GetRadiosAsync();
            _wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            _bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (_wifi != null) _wifi.StateChanged += (_, _) => Update();
            if (_bt != null) _bt.StateChanged += (_, _) => Update();
        }
        catch (Exception ex) { App.Log(ex); }
        Update();
    }

    private void Update() => Ui.Run(() =>
    {
        WifiOn = _wifi?.State == RadioState.On;
        BluetoothOn = _bt?.State == RadioState.On;
        Raise(nameof(HasWifi)); Raise(nameof(HasBluetooth));
    });

    public async Task SetWifi(bool on) { try { if (_wifi != null) await _wifi.SetStateAsync(on ? RadioState.On : RadioState.Off); } catch { } Update(); }
    public async Task SetBluetooth(bool on) { try { if (_bt != null) await _bt.SetStateAsync(on ? RadioState.On : RadioState.Off); } catch { } Update(); }
}

/// <summary>Built-in display brightness via WMI (laptops / tablets only).</summary>
public sealed class BrightnessService : ObservableObject
{
    private int _level;
    private bool _supported, _updating;
    private readonly DispatcherTimer _apply = new() { Interval = TimeSpan.FromMilliseconds(120) };

    public bool Supported { get => _supported; private set => Set(ref _supported, value); }

    public int Level
    {
        get => _level;
        set
        {
            if (!Set(ref _level, value) || _updating) return;
            _apply.Stop(); _apply.Start();
        }
    }

    public BrightnessService()
    {
        _apply.Tick += (_, _) =>
        {
            _apply.Stop();
            int v = _level;
            Task.Run(() =>
            {
                try
                {
                    using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                    foreach (ManagementObject o in s.Get()) { o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v }); break; }
                }
                catch { }
            });
        };
    }

    public Task RefreshAsync() => Task.Run(() =>
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get())
            {
                int l = Convert.ToInt32(o["CurrentBrightness"]);
                Ui.Run(() => { _updating = true; Level = l; _updating = false; Supported = true; });
                return;
            }
        }
        catch { }
        Ui.Run(() => Supported = false);
    });
}

/// <summary>Windows 11 power mode (efficiency / balanced / performance overlay).</summary>
public static class PowerMode
{
    private static readonly (string Name, Guid Id)[] Modes =
    {
        ("Effizienz", new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a")),
        ("Ausgeglichen", Guid.Empty),
        ("Leistung", new Guid("ded574b5-45a0-4f42-8737-46345c09c238")),
    };

    public static string Current()
    {
        try
        {
            if (Native.PowerGetEffectiveOverlayScheme(out var g) == 0)
                return Modes.FirstOrDefault(m => m.Id == g).Name ?? "Ausgeglichen";
        }
        catch { }
        return "Ausgeglichen";
    }

    public static string Cycle()
    {
        var cur = Current();
        int i = Array.FindIndex(Modes, m => m.Name == cur);
        var next = Modes[(i + 1) % Modes.Length];
        try { Native.PowerSetActiveOverlayScheme(next.Id); } catch { }
        return Current();
    }
}

/// <summary>Detects a fullscreen app/game on the primary monitor.</summary>
public sealed class GameModeService : ObservableObject
{
    private readonly ForegroundTracker _fg;
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private bool _full;
    private static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow" };

    public bool IsFullscreen { get => _full; private set => Set(ref _full, value); }

    public GameModeService(ForegroundTracker fg) { _fg = fg; }

    public void Start()
    {
        _t.Tick += (_, _) => Check();
        _fg.Changed += _ => Check();
        _t.Start();
    }

    private void Check()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) { IsFullscreen = false; return; }
        if (_fg.IsOwn(h)) return;
        if (ShellClasses.Contains(Native.ClassNameOf(h))) { IsFullscreen = false; return; }
        if (!Native.GetWindowRect(h, out var r)) return;
        var mon = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return;
        bool primary = (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0;
        var m = mi.rcMonitor;
        bool covers = primary && r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        // with an auto-hiding taskbar every MAXIMIZED window covers the whole monitor too – that is a normal app
        // window (title bar / resize frame), not a fullscreen game or video
        if (covers && IsZoomed(h) && (Native.GetWindowLongPtr(h, GWL_STYLE).ToInt64() & (WS_CAPTION | WS_THICKFRAME)) != 0) covers = false;
        IsFullscreen = covers;
    }

    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
}

