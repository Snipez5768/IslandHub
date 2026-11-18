using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Printing;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>A device with its own battery (controller, headphones, mouse …).</summary>
public sealed record DeviceBattery(string Name, string Icon, double Level);

/// <summary>A USB stick or card that was plugged in.</summary>
public sealed record RemovableDrive(string Root, string Label, long Free);

/// <summary>
/// The system block (user's picks 2026-11-18): batteries of devices (23), network speed, Wi-Fi and VPN (24), a
/// Windows update that waits for a restart (25), USB sticks (26), the printer (27), temperatures while playing (28)
/// and how long the laptop battery lasts (29). Everything is read on this PC; nothing is sent anywhere.
/// </summary>
public sealed class SystemWatch : ObservableObject
{
    private readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _tick;

    public event Action<string> WifiChanged;          // name (or "") – null = disconnected
    public event Action<bool> VpnChanged;
    public event Action<RemovableDrive> DriveAdded;
    public event Action<string> DriveRemoved;
    public event Action PrintDone;
    public event Action<string> PrintProblem;
    public event Action<DeviceBattery> DeviceCritical;  // 10 % – once per device
    public event Action UpdateWaiting;                 // once a day

    public void Start()
    {
        _fast.Tick += (_, _) => Tick();
        _fast.Start();
        NetworkChange.NetworkAddressChanged += (_, _) => Ui.Run(() => _netDirty = DateTime.Now.AddSeconds(2));
        _drives = Removables().Select(d => d.Root).ToHashSet();
        ReadNetwork(initial: true);
        _ = ReadBatteriesAsync();
    }

    private void Tick()
    {
        _tick++;
        Speeds();
        if (_netDirty is { } t && DateTime.Now >= t) { _netDirty = null; ReadNetwork(initial: false); }
        if (_tick % 2 == 0) Drives();
        if (_tick % 3 == 0) Printers();
        if (_tick % 5 == 0) BatteryForecast();
        if (_tick % 5 == 0 && App.Hub?.Game?.IsFullscreen == true) _ = TemperaturesAsync();
        if (_tick % 60 == 0) _ = ReadBatteriesAsync();
        if (_tick % 600 == 5) CheckUpdate();
    }

    // =====================================================================
    //  24 · network
    // =====================================================================

    private long _rx, _tx;
    private DateTime _at;
    private DateTime? _netDirty;
    private string _wifi = "?";
    private bool? _vpn;

    public double Down { get; private set; }   // bytes per second
    public double Up { get; private set; }
    public string Wifi => _wifi == "?" ? null : _wifi;
    public bool Vpn => _vpn == true;
    public bool Online { get; private set; }

    private void Speeds()
    {
        try
        {
            long rx = 0, tx = 0;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = n.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            var now = DateTime.Now;
            if (_at != default)
            {
                double dt = Math.Max(0.2, (now - _at).TotalSeconds);
                Down = Math.Max(0, (rx - _rx) / dt);
                Up = Math.Max(0, (tx - _tx) / dt);
                Raise(nameof(Down));
            }
            _rx = rx; _tx = tx; _at = now;
        }
        catch { }
    }

    private static readonly string[] VpnMarks = { "vpn", "wireguard", "wintun", "tap-", "openvpn", "anyconnect", "tailscale", "nordlynx", "proton", "fortinet", "globalprotect" };

    private void ReadNetwork(bool initial)
    {
        bool vpn = false, wifiUp = false;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                var d = (n.Description + " " + n.Name).ToLowerInvariant();
                if (n.NetworkInterfaceType == NetworkInterfaceType.Ppp || VpnMarks.Any(d.Contains)) vpn = true;
                if (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) wifiUp = true;
            }
        }
        catch { }
        Online = NetworkInterface.GetIsNetworkAvailable();
        string wifi = wifiUp ? SsidOrEmpty() : null;
        if (!initial && wifi != _wifi && _wifi != "?") WifiChanged?.Invoke(wifi);
        if (!initial && _vpn != null && vpn != _vpn) VpnChanged?.Invoke(vpn);
        _wifi = wifi ?? "";
        if (!wifiUp) _wifi = null;
        _vpn = vpn;
        Raise(nameof(Wifi));
    }

    /// <summary>The Wi-Fi's name – Windows only tells it when location is allowed; otherwise "".</summary>
    private static string SsidOrEmpty()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            foreach (var line in text.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("SSID-", StringComparison.OrdinalIgnoreCase) && l.Contains(':'))
                    return l[(l.IndexOf(':') + 1)..].Trim();
            }
        }
        catch { }
        return "";
    }

    // =====================================================================
    //  26 · USB sticks
    // =====================================================================

    private HashSet<string> _drives = new();

    public static List<RemovableDrive> Removables()
    {
        var list = new List<RemovableDrive>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Removable || !d.IsReady) continue;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? L.T("USB-Stick") : d.VolumeLabel;
                list.Add(new RemovableDrive(d.RootDirectory.FullName, label, d.AvailableFreeSpace));
            }
        }
        catch { }
        return list;
    }

    private void Drives()
    {
        var now = Removables();
        var roots = now.Select(d => d.Root).ToHashSet();
        foreach (var d in now.Where(d => !_drives.Contains(d.Root))) DriveAdded?.Invoke(d);
        foreach (var gone in _drives.Where(r => !roots.Contains(r))) DriveRemoved?.Invoke(gone);
        _drives = roots;
    }

    /// <summary>Safely remove – the same as "Auswerfen" in the Explorer.</summary>
    public static void Eject(string root)
    {
        try
        {
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            dynamic folder = shell.GetType().InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { 17 });
            dynamic item = folder.ParseName(root);
            item.InvokeVerb("Eject");
        }
        catch { }
    }

    // =====================================================================
    //  27 · printer
    // =====================================================================

    public bool Printing { get; private set; }
    public double PrintProgress { get; private set; }
    public string PrintText { get; private set; } = "";
    private string _printProblem;

    private void Printers()
    {
        bool printing = false;
        double progress = 0;
        string text = "", problem = null;
        try
        {
            using var server = new LocalPrintServer();
            foreach (var q in server.GetPrintQueues())
            {
                using (q)
                {
                    q.Refresh();
                    if (q.NumberOfJobs > 0)
                    {
                        foreach (var j in q.GetPrintJobInfoCollection())
                        {
                            using (j)
                            {
                                printing = true;
                                int total = Math.Max(1, j.NumberOfPages);
                                progress = Math.Clamp((double)j.NumberOfPagesPrinted / total, 0, 1);
                                text = total > 1 ? $"{Math.Min(total, j.NumberOfPagesPrinted + 1)} / {total}" : L.T("Druckt");
                                break;
                            }
                        }
                    }
                    if (q.IsOutOfPaper) problem = L.T("Papier leer");
                    else if (q.IsPaperJammed) problem = L.T("Papierstau");
                    else if (q.IsTonerLow && printing) problem = L.T("Toner fast leer");
                    else if (q.IsInError && printing) problem = L.T("Drucker-Fehler");
                }
            }
        }
        catch { }
        if (problem != null && problem != _printProblem) PrintProblem?.Invoke(problem);
        _printProblem = problem;
        if (Printing && !printing && problem == null) PrintDone?.Invoke();
        bool changed = printing != Printing || Math.Abs(progress - PrintProgress) > 0.001 || text != PrintText;
        Printing = printing; PrintProgress = progress; PrintText = text;
        if (changed) Raise(nameof(Printing));
    }

    // =====================================================================
    //  23 · batteries of devices
    // =====================================================================

    public List<DeviceBattery> Devices { get; private set; } = new();
    public DeviceBattery LowDevice => Devices.Where(d => d.Level <= 0.2).OrderBy(d => d.Level).FirstOrDefault();
    private readonly HashSet<string> _warned = new();

    private async Task ReadBatteriesAsync()
    {
        var list = new List<DeviceBattery>();
        // Xbox controllers (XInput)
        for (int i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetBatteryInformation(i, 0, out var b) != 0 || b.Type is 0 or 1) continue; // disconnected / wired
                double level = b.Level switch { 0 => 0.05, 1 => 0.2, 2 => 0.55, _ => 1 };
                list.Add(new DeviceBattery(i == 0 ? L.T("Controller") : L.T($"Controller {i + 1}"), "gamecontroller", level));
            }
            catch (DllNotFoundException) { break; }
            catch { }
        }
        // Bluetooth devices that report their battery
        try
        {
            const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
            var all = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                "System.Devices.Present:=System.StructuredQueryType.Boolean#True", new[] { BatteryKey },
                Windows.Devices.Enumeration.DeviceInformationKind.Device);
            foreach (var d in all)
            {
                if (!d.Properties.TryGetValue(BatteryKey, out var v) || v is not byte pct) continue;
                if (list.Any(x => x.Name == d.Name)) continue;
                var n = d.Name.ToLowerInvariant();
                string icon = n.Contains("mouse") || n.Contains("maus") || n.Contains("mx ") ? "cursor"
                            : n.Contains("keyboard") || n.Contains("tastatur") || n.Contains("keys") ? "keyboard"
                            : n.Contains("controller") || n.Contains("gamepad") ? "gamecontroller" : "headphones";
                list.Add(new DeviceBattery(d.Name, icon, pct / 100.0));
            }
        }
        catch { }
        Devices = list;
        foreach (var d in list.Where(d => d.Level <= 0.1 && _warned.Add(d.Name))) DeviceCritical?.Invoke(d);
        foreach (var d in list.Where(d => d.Level > 0.3)) _warned.Remove(d.Name);
        Raise(nameof(Devices));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XBattery { public byte Type; public byte Level; }
    [DllImport("xinput1_4.dll")] private static extern int XInputGetBatteryInformation(int user, byte devType, out XBattery info);

    // =====================================================================
    //  29 · how long the laptop battery lasts
    // =====================================================================

    private readonly Queue<(DateTime At, double Pct)> _charge = new();
    public bool HasBattery { get; private set; }
    public string Forecast { get; private set; }

    private void BatteryForecast()
    {
        try
        {
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            HasBattery = ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery && ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Unknown;
            if (!HasBattery) { Forecast = null; return; }
            double pct = ps.BatteryLifePercent;
            bool charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            if (!charging)
            {
                _charge.Clear();
                Forecast = ps.BatteryLifeRemaining > 0 ? L.T($"noch {Span(ps.BatteryLifeRemaining)}") : null;
            }
            else
            {
                // Windows does not say when it is full – the last minutes of charging tell
                _charge.Enqueue((DateTime.Now, pct));
                while (_charge.Count > 0 && DateTime.Now - _charge.Peek().At > TimeSpan.FromMinutes(6)) _charge.Dequeue();
                var first = _charge.Peek();
                double rate = (pct - first.Pct) / Math.Max(1, (DateTime.Now - first.At).TotalSeconds); // per second
                Forecast = pct >= 0.995 ? L.T("voll geladen")
                         : rate > 0 && DateTime.Now - first.At > TimeSpan.FromMinutes(2) ? L.T($"voll in {Span((int)((1 - pct) / rate))}") : null;
            }
        }
        catch { Forecast = null; }
    }

    private static string Span(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(1, t.Minutes)} min";
    }

    // =====================================================================
    //  28 · temperatures while playing
    // =====================================================================

    public double? GpuTemp { get; private set; }
    public double? CpuTemp { get; private set; }
    private bool _noNvidia, _noCpuTemp;

    private async Task TemperaturesAsync()
    {
        await Task.Run(() =>
        {
            if (!_noNvidia)
            {
                try
                {
                    var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=temperature.gpu --format=csv,noheader,nounits")
                        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                    using var p = Process.Start(psi);
                    var s = p.StandardOutput.ReadToEnd().Trim().Split('\n')[0];
                    p.WaitForExit(2000);
                    GpuTemp = double.TryParse(s, out var v) ? v : null;
                    if (GpuTemp == null) _noNvidia = true;
                }
                catch { _noNvidia = true; GpuTemp = null; }
            }
            if (!_noCpuTemp)
            {
                try
                {
                    using var q = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                    double max = 0;
                    foreach (var o in q.Get()) max = Math.Max(max, System.Convert.ToDouble(o["CurrentTemperature"]) / 10 - 273.15);
                    CpuTemp = max > 5 ? max : null;
                    if (CpuTemp == null) _noCpuTemp = true;
                }
                catch { _noCpuTemp = true; CpuTemp = null; }
            }
        });
        Raise(nameof(GpuTemp));
    }

    // =====================================================================
    //  25 · a Windows update waits for a restart
    // =====================================================================

    public bool RestartPending { get; private set; }
    private DateTime _updateToldOn;

    private void CheckUpdate()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            RestartPending = k != null;
        }
        catch { RestartPending = false; }
        if (RestartPending && _updateToldOn != DateTime.Today && DateTime.Now.Hour >= 9) { _updateToldOn = DateTime.Today; UpdateWaiting?.Invoke(); }
    }

    /// <summary>Restart at a time (or in a few seconds) – Windows' own planned shutdown, which "Abbrechen" undoes.</summary>
    public static void RestartAt(DateTime at)
    {
        int secs = (int)Math.Max(5, (at - DateTime.Now).TotalSeconds);
        try { Process.Start(new ProcessStartInfo("shutdown", $"/r /t {secs}") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
    }

    public static void CancelRestart()
    {
        try { Process.Start(new ProcessStartInfo("shutdown", "/a") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
    }
}
