using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace PeakPillSetup;

/// <summary>
/// PeakPill-Setup.exe                    – the installer window
/// PeakPill-Setup.exe /quiet [/launch]   – installs without a window (defaults: autostart on, no desktop entry)
/// Deinstallieren.exe /uninstall [/quiet] – removes PeakPill (data is kept unless the window says otherwise)
/// PeakPill-Setup.exe /shots &lt;dir&gt;      – pictures of every page (for checking the layout)
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToList();
        bool quiet = args.Contains("/quiet"), uninstall = args.Contains("/uninstall");

        int shots = args.IndexOf("/shots");
        if (shots >= 0 && shots + 1 < e.Args.Length)
        {
            var w = new MainWindow(false);
            MainWindow = w;
            w.Show();
            await w.ShotsAsync(e.Args[shots + 1]);
            Shutdown();
            return;
        }

        if (uninstall)
        {
            // the uninstaller cannot delete the file it runs from: it hands over to a copy in the temp folder
            var self = Assembly.GetExecutingAssembly().Location;
            if (!args.Contains("/fromtemp"))
            {
                var tmp = Path.Combine(Path.GetTempPath(), "PeakPill-Deinstallieren.exe");
                try
                {
                    File.Copy(self, tmp, true);
                    Process.Start(tmp, "/uninstall /fromtemp" + (quiet ? " /quiet" : ""));
                    Shutdown();
                    return;
                }
                catch { /* go on from here – only the uninstaller file itself stays behind */ }
            }
            if (quiet)
            {
                Installer.Uninstall(keepData: true);
                Installer.DeleteSelfLater();
                Shutdown();
                return;
            }
        }
        else if (quiet)
        {
            try
            {
                await Installer.InstallAsync(new Installer.Options { Autostart = true }, new Progress<(double, string)>());
                if (args.Contains("/launch")) Installer.Launch();
                Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }

        var win = new MainWindow(uninstall);
        MainWindow = win;
        win.Show();
    }
}
