using System.Management;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using PeakPill.Core;
using WF = System.Windows.Forms;

namespace PeakPill.Services;

/// <summary>
/// The small system moments Apple shows in the island (polish 2026-11-15): volume and brightness changes, headphones
/// connected (with their battery when Windows knows it), plugged in / battery low. Everything is event-driven –
/// nothing is polled.
/// </summary>
public sealed class DeviceMoments : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _en = new();
    private MMDevice _speaker;
    private ManagementEventWatcher _brightWatch;
    private DateTime _quietUntil = DateTime.Now.AddSeconds(4); // the first events after start are not news

    /// <summary>Speaker volume 0…1 and mute, from any source (keys, mixer, apps).</summary>
    public event Action<double, bool> Volume;
    /// <summary>Laptop screen brightness 0…1.</summary>
    public event Action<double> Brightness;
    /// <summary>Name of the headphones that became the sound output, and their battery (0…1) if Windows knows it.</summary>
    public event Action<string, double?> Headphones;
    /// <summary>Charger plugged in (true) / pulled (false), with the battery level 0…1.</summary>
    public event Action<bool, double> Charger;
    /// <summary>The battery dropped to 20 % or 10 % (0…1).</summary>
    public event Action<double> BatteryLow;

    private bool _onPower;
    private int _lowWarned = 100;

    public void Start()
    {
        WatchSpeaker();
        try { _en.RegisterEndpointNotificationCallback(this); } catch (Exception ex) { App.Log(ex); }
        try
        {
            _brightWatch = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _brightWatch.EventArrived += (_, e) =>
            {
                try
                {
                    double v = Convert.ToDouble(e.NewEvent["Brightness"]) / 100.0;
                    Ui.Run(() => { if (DateTime.Now > _quietUntil) Brightness?.Invoke(v); });
                }
                catch { }
            };
            _brightWatch.Start();
        }
        catch { _brightWatch = null; } // desktop monitors: no WMI brightness
        var ps = WF.SystemInformation.PowerStatus;
        _onPower = ps.PowerLineStatus == WF.PowerLineStatus.Online;
        SystemEvents.PowerModeChanged += OnPowerMode;
    }

    private void WatchSpeaker()
    {
        try
        {
            if (_speaker != null) { _speaker.AudioEndpointVolume.OnVolumeNotification -= OnVolume; _speaker.Dispose(); }
            _speaker = _en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _speaker.AudioEndpointVolume.OnVolumeNotification += OnVolume;
        }
        catch { _speaker = null; }
    }

    private void OnVolume(AudioVolumeNotificationData d)
    {
        double v = d.MasterVolume;
        bool muted = d.Muted;
        Ui.Run(() => { if (DateTime.Now > _quietUntil) Volume?.Invoke(v, muted); });
    }

    private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        var ps = WF.SystemInformation.PowerStatus;
        if (ps.BatteryChargeStatus.HasFlag(WF.BatteryChargeStatus.NoSystemBattery)) return;
        double level = Math.Clamp(ps.BatteryLifePercent, 0, 1);
        bool power = ps.PowerLineStatus == WF.PowerLineStatus.Online;
        Ui.Run(() =>
        {
            if (power != _onPower)
            {
                _onPower = power;
                if (power) _lowWarned = 100;
                Charger?.Invoke(power, level);
                return;
            }
            int pct = (int)Math.Round(level * 100);
            int step = pct <= 10 ? 10 : pct <= 20 ? 20 : 100;
            if (!power && step < _lowWarned) { _lowWarned = step; BatteryLow?.Invoke(level); }
        });
    }

    // ---------- IMMNotificationClient: the sound output changed ----------

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render || role != Role.Multimedia) return;
        Ui.Run(WatchSpeaker);
        if (string.IsNullOrEmpty(defaultDeviceId)) return;
        Task.Run(async () =>
        {
            try
            {
                using var dev = _en.GetDevice(defaultDeviceId);
                if (!IsHeadphones(dev, out bool bluetooth)) return;
                string name = Clean(dev.DeviceFriendlyName);
                double? battery = bluetooth ? await BluetoothBatteryAsync(name) : null;
                Ui.Run(() => { if (DateTime.Now > _quietUntil) Headphones?.Invoke(name, battery); });
            }
            catch { }
        });
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    /// <summary>Headphones or a headset (form factor), or any Bluetooth audio device.</summary>
    private static readonly PropertyKey EnumeratorName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 24); // PKEY_Device_EnumeratorName

    private static bool IsHeadphones(MMDevice d, out bool bluetooth)
    {
        bluetooth = false;
        try
        {
            var id = d.ID ?? "";
            var en = d.Properties.Contains(EnumeratorName) ? d.Properties[EnumeratorName].Value as string : null;
            bluetooth = string.Equals(en, "BTHENUM", StringComparison.OrdinalIgnoreCase) || string.Equals(en, "BTHHFENUM", StringComparison.OrdinalIgnoreCase)
                        || id.Contains("BTH", StringComparison.OrdinalIgnoreCase);
            int form = d.Properties.Contains(PropertyKeys.PKEY_AudioEndpoint_FormFactor) ? Convert.ToInt32(d.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value) : -1;
            return bluetooth || form is 3 or 5; // Headphones, Headset
        }
        catch { return false; }
    }

    private static string Clean(string name)
    {
        name = (name ?? "").Trim();
        foreach (var junk in new[] { " Stereo", " Hands-Free AG Audio", " Hands-Free", " (Stereo)" })
            if (name.EndsWith(junk, StringComparison.OrdinalIgnoreCase)) name = name[..^junk.Length].Trim();
        return name.Length == 0 ? "Kopfhörer" : name;
    }

    /// <summary>Battery of a Bluetooth device by name (DEVPKEY_Bluetooth_Battery) – null when Windows does not know it.</summary>
    private static async Task<double?> BluetoothBatteryAsync(string name)
    {
        const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        try
        {
            var all = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                "System.Devices.Present:=System.StructuredQueryType.Boolean#True", new[] { BatteryKey },
                Windows.Devices.Enumeration.DeviceInformationKind.Device);
            foreach (var di in all)
            {
                if (di.Name == null || !di.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && !name.Contains(di.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (di.Properties.TryGetValue(BatteryKey, out var v) && v != null) return Math.Clamp(Convert.ToDouble(v) / 100.0, 0, 1);
            }
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        try { _en.UnregisterEndpointNotificationCallback(this); } catch { }
        try { _brightWatch?.Stop(); _brightWatch?.Dispose(); } catch { }
        SystemEvents.PowerModeChanged -= OnPowerMode;
    }
}
