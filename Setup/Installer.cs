using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PeakPillSetup;

/// <summary>
/// What the installer does – for the current user only, no admin: PeakPill.exe goes to
/// %LocalAppData%\Programs\PeakPill, a Start-menu entry (and on wish a desktop one), autostart, and an entry in
/// "Apps › Installierte Apps" that removes everything again. The .NET 8 desktop runtime is installed from Microsoft
/// when it is missing (Windows asks once). Installs from the IslandHub days (the name until 1.2.0) are taken over.
/// </summary>
public static class Installer
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PeakPill");
    public static string Exe => Path.Combine(Dir, "PeakPill.exe");
    public static string Uninstaller => Path.Combine(Dir, "Deinstallieren.exe");
    public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "PeakPill.lnk");
    public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PeakPill.lnk");
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeakPill");
    private static readonly string OldDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandHub");

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PeakPill";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string RuntimeUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";
    public const string RepoUrl = "https://github.com/Snipez5768/PeakPill";

    public static string Version
    {
        get { var v = Assembly.GetExecutingAssembly().GetName().Version; return $"{v.Major}.{v.Minor}.{v.Build}"; }
    }

    /// <summary>The version already installed with this installer (null: none).</summary>
    public static string InstalledVersion
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return File.Exists(Exe) ? k?.GetValue("DisplayVersion") as string ?? "?" : null;
            }
            catch { return null; }
        }
    }

    /// <summary>PeakPill was used before under its old name (an IslandHub.exe somewhere, its data or autostart).</summary>
    public static bool HadIslandHub
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue("IslandHub") != null || Directory.Exists(OldDataDir) || Process.GetProcessesByName("IslandHub").Length > 0;
            }
            catch { return false; }
        }
    }

    /// <summary>Autostart is on right now (PeakPill's own entry, or the old one).</summary>
    public static bool AutostartOn
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue("PeakPill") != null || k?.GetValue("IslandHub") != null;
            }
            catch { return false; }
        }
    }

    /// <summary>The .NET 8 desktop runtime (64 bit) PeakPill runs on is not there yet.</summary>
    public static bool RuntimeMissing
    {
        get
        {
            try
            {
                var pf = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var dir = Path.Combine(pf, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                return !Directory.Exists(dir) || !Directory.GetDirectories(dir).Any(d => Path.GetFileName(d).StartsWith("8.", StringComparison.Ordinal));
            }
            catch { return true; }
        }
    }

    // ------------------------------------------------------------------ install

    public sealed class Options
    {
        public bool Autostart = true;
        public bool DesktopLink;
    }

    public static async Task InstallAsync(Options o, IProgress<(double Part, string Text)> p)
    {
        if (RuntimeMissing) await InstallRuntimeAsync(p);

        p.Report((0.55, "PeakPill wird beendet …"));
        await Task.Run(StopRunning);

        p.Report((0.65, "PeakPill wird kopiert …"));
        Directory.CreateDirectory(Dir);
        await Task.Run(() => Retry(() =>
        {
            using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.gz")
                            ?? throw new InvalidOperationException("Diesem Installer fehlt PeakPill selbst.");
            using var gz = new GZipStream(src, CompressionMode.Decompress);
            var tmp = Exe + ".new";
            using (var dst = File.Create(tmp)) gz.CopyTo(dst);
            if (File.Exists(Exe)) File.Delete(Exe);
            File.Move(tmp, Exe);
        }));
        var self = Assembly.GetExecutingAssembly().Location;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(Uninstaller), StringComparison.OrdinalIgnoreCase))
            Retry(() => File.Copy(self, Uninstaller, true));

        p.Report((0.82, "Verknüpfungen …"));
        Shortcut(StartMenuLink);
        if (o.DesktopLink) Shortcut(DesktopLink);
        else TryDelete(DesktopLink);

        p.Report((0.9, "Autostart …"));
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
        {
            run.DeleteValue("IslandHub", false); // the old name – PeakPill takes its place
            approved?.DeleteValue("IslandHub", false);
            if (o.Autostart)
            {
                run.SetValue("PeakPill", $"\"{Exe}\" --autostart");
                approved?.DeleteValue("PeakPill", false); // switched on here = really on
            }
            else run.DeleteValue("PeakPill", false);
        }

        p.Report((0.96, "Eintrag in den Apps …"));
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "PeakPill");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "PeakPill");
            k.SetValue("DisplayIcon", $"\"{Exe}\",0");
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", $"\"{Uninstaller}\" /uninstall");
            k.SetValue("QuietUninstallString", $"\"{Uninstaller}\" /uninstall /quiet");
            k.SetValue("URLInfoAbout", RepoUrl);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
        }
        p.Report((1, "Fertig"));
    }

    /// <summary>Downloads Microsoft's .NET 8 desktop runtime and installs it (Windows asks for permission once).</summary>
    private static async Task InstallRuntimeAsync(IProgress<(double, string)> p)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var file = Path.Combine(Path.GetTempPath(), "PeakPill-dotnet8-desktop.exe");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var resp = await http.GetAsync(RuntimeUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
            using var src = await resp.Content.ReadAsStreamAsync();
            using var dst = File.Create(file);
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                await dst.WriteAsync(buf, 0, n);
                done += n;
                if (total > 0) p.Report((0.4 * done / total, $".NET 8 von Microsoft wird geladen … {done / 1048576} von {total / 1048576} MB"));
            }
        }
        p.Report((0.42, ".NET 8 wird installiert – Windows fragt einmal nach Erlaubnis …"));
        int code;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(file, "/install /quiet /norestart") { UseShellExecute = true });
            await Task.Run(() => proc.WaitForExit());
            code = proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Die Erlaubnis für .NET wurde nicht gegeben. PeakPill braucht .NET 8 – starte den Installer einfach noch einmal.");
        }
        finally { TryDelete(file); }
        if (code != 0 && code != 3010 && RuntimeMissing)
            throw new InvalidOperationException($".NET 8 konnte nicht installiert werden (Fehler {code}). Du kannst es auch selbst von dotnet.microsoft.com laden („.NET Desktop Runtime 8“).");
    }

    /// <summary>PeakPill (and a copy from its IslandHub days) must not run while its file is replaced.</summary>
    private static void StopRunning()
    {
        int me = Process.GetCurrentProcess().Id;
        foreach (var name in new[] { "PeakPill", "IslandHub" })
            foreach (var proc in Process.GetProcessesByName(name))
                using (proc)
                {
                    if (proc.Id == me) continue;
                    try { proc.Kill(); proc.WaitForExit(4000); } catch { }
                }
        Thread.Sleep(300); // Windows lets go of the file a moment later
    }

    public static void Launch()
    {
        try { Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true, WorkingDirectory = Dir }); } catch { }
    }

    // ------------------------------------------------------------------ uninstall

    public static void Uninstall(bool keepData)
    {
        StopRunning();
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) run?.DeleteValue("PeakPill", false);
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true)) approved?.DeleteValue("PeakPill", false);
        TryDelete(StartMenuLink);
        TryDelete(DesktopLink);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        var self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
        if (Directory.Exists(Dir))
        {
            foreach (var f in Directory.GetFiles(Dir, "*", SearchOption.AllDirectories))
                if (!string.Equals(Path.GetFullPath(f), self, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
            try { Directory.Delete(Dir, true); } catch { }
        }
        if (!keepData)
            foreach (var d in new[] { DataDir, OldDataDir })
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    }

    /// <summary>The uninstaller runs from a copy in the temp folder (it cannot delete the file it runs from);
    /// that copy removes itself a few seconds after it closed.</summary>
    public static void DeleteSelfLater()
    {
        try
        {
            var self = Assembly.GetExecutingAssembly().Location;
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 >nul & del /f /q \"{self}\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { }
    }

    // ------------------------------------------------------------------ helpers

    private static void Shortcut(string path)
    {
        object shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic sh = shell;
            link = sh.CreateShortcut(path);
            dynamic l = link;
            l.TargetPath = Exe;
            l.WorkingDirectory = Dir;
            l.IconLocation = Exe + ",0";
            l.Description = "PeakPill – eine Dynamic Island für Windows";
            l.Save();
        }
        catch { /* a missing shortcut is no reason to fail the install */ }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void Retry(Action a)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) when (i < 20) { Thread.Sleep(250); }
        }
    }

    private static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }
}
