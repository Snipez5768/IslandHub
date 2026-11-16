using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace PeakPill.Services;

/// <summary>
/// The volume of ONE app (its slider in the Windows volume mixer) – e.g. only Spotify, the system volume stays.
/// Sessions are looked up once per <see cref="Attach"/> and reused while the slider moves.
/// </summary>
public sealed class AppVolume
{
    private readonly List<AudioSessionControl> _sessions = new();
    private readonly List<MMDevice> _devices = new();

    public string ProcessName { get; private set; }
    public bool Available => _sessions.Count > 0;

    /// <summary>Finds every audio session of the process on every output device. False if it has none.</summary>
    public bool Attach(string processName)
    {
        Release();
        ProcessName = processName;
        try
        {
            var ids = Process.GetProcessesByName(processName).Select(p => { using (p) return (uint)p.Id; }).ToHashSet();
            if (ids.Count == 0) return false;
            var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                bool keep = false;
                try
                {
                    var mgr = dev.AudioSessionManager;
                    mgr.RefreshSessions();
                    var ss = mgr.Sessions;
                    for (int i = 0; i < ss.Count; i++)
                    {
                        var s = ss[i];
                        if (ids.Contains(s.GetProcessID) && s.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired)
                        {
                            _sessions.Add(s);
                            keep = true;
                        }
                    }
                }
                catch { }
                if (keep) _devices.Add(dev); else (dev as IDisposable)?.Dispose();
            }
        }
        catch { }
        return Available;
    }

    /// <summary>0…1, from the active session if there is one.</summary>
    public double Volume
    {
        get
        {
            try
            {
                var s = _sessions.FirstOrDefault(x => x.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) ?? _sessions.FirstOrDefault();
                return s?.SimpleAudioVolume.Volume ?? 1;
            }
            catch { return 1; }
        }
        set
        {
            var v = (float)Math.Clamp(value, 0, 1);
            foreach (var s in _sessions)
            {
                try
                {
                    s.SimpleAudioVolume.Volume = v;
                    if (v > 0 && s.SimpleAudioVolume.Mute) s.SimpleAudioVolume.Mute = false;
                }
                catch { }
            }
        }
    }

    /// <summary>Current loudness 0…1 (the loudest of the app's sessions) – drives the bar chart in the island.</summary>
    public double Peak
    {
        get
        {
            double p = 0;
            foreach (var s in _sessions)
            {
                try { p = Math.Max(p, s.AudioMeterInformation.MasterPeakValue); } catch { }
            }
            return p;
        }
    }

    public void Release()
    {
        foreach (var s in _sessions) (s as IDisposable)?.Dispose();
        foreach (var d in _devices) (d as IDisposable)?.Dispose();
        _sessions.Clear();
        _devices.Clear();
    }
}
