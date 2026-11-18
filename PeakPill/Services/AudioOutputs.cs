using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PeakPill.Services;

/// <summary>One sound output as the island shows it: a short name and a symbol (headphones / screen / speaker).</summary>
public sealed record AudioOutput(string Id, string Name, string Icon);

/// <summary>
/// Switches the Windows sound output to the next device (18b, 2026-11-18). Windows has no public call for that – this
/// uses the policy interface the Windows sound settings use themselves (the same way other sound switchers do).
/// </summary>
public static class AudioOutputs
{
    public static List<AudioOutput> All(out string currentId)
    {
        currentId = null;
        var list = new List<AudioOutput>();
        try
        {
            using var en = new MMDeviceEnumerator();
            try { currentId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) list.Add(Describe(d));
        }
        catch { }
        return list;
    }

    /// <summary>The next device becomes the output (for music, calls and system sounds). Null when there is only one.</summary>
    public static (AudioOutput From, AudioOutput To)? SwitchNext()
    {
        var list = All(out var cur);
        if (list.Count < 2) return null;
        int i = Math.Max(0, list.FindIndex(d => d.Id == cur));
        var to = list[(i + 1) % list.Count];
        try
        {
            var policy = (IPolicyConfig)new CPolicyConfigClient();
            foreach (var role in new[] { 0, 1, 2 }) policy.SetDefaultEndpoint(to.Id, role); // console, multimedia, communications
        }
        catch { return null; }
        return (list[i], to);
    }

    private static AudioOutput Describe(MMDevice d)
    {
        string friendly = d.FriendlyName ?? "";
        bool phones = DeviceMoments.IsHeadphones(d, out _);
        string name = phones ? DeviceMoments.Clean(d.DeviceFriendlyName) : friendly.Split(" (")[0].Trim();
        if (name.Length == 0) name = friendly;
        string all = (friendly + " " + d.DeviceFriendlyName).ToUpperInvariant();
        string icon = phones ? "headphones"
                    : all.Contains("HDMI") || all.Contains("DISPLAY") || all.Contains("NVIDIA HIGH DEFINITION") || all.Contains("AMD HIGH DEFINITION") || all.Contains("MONITOR") ? "display"
                    : "speaker";
        return new AudioOutput(d.ID, name, icon);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient { }

    // the order of the methods matters (it is the vtable) – only SetDefaultEndpoint is used
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpoint, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool def, IntPtr defPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
    }
}
