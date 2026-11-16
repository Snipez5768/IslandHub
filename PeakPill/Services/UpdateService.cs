using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>
/// Updates from GitHub (user wish 2026-11-15): on start (and every 6 hours) PeakPill asks GitHub for the newest
/// release. Is it newer, the island shows a notice; "Installieren" downloads the new PeakPill.exe, swaps it in
/// once PeakPill has closed and starts the new version. Only the public release list is read – nothing is sent.
/// </summary>
public sealed class UpdateService
{
    /// <summary>owner/repository on GitHub.</summary>
    public const string Repo = "Snipez5768/PeakPill";

    public sealed record Release(Version Version, string Tag, string Name, string Notes, string ExeUrl, string Page);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private System.Windows.Threading.DispatcherTimer _t;

    public static Version Current => typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>A newer release was found (raised on the UI thread).</summary>
    public event Action<Release> Available;

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("PeakPill-Updater");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public void Start()
    {
        // (1.0.0 still had a leftover guard here that was always true – the automatic check never ran, only the button)
        if (!App.Settings.UpdateCheck) return;
        _t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _t.Tick += async (_, _) => await CheckAsync();
        _t.Start();
        // a little after start – the island first settles in
        _ = Task.Delay(TimeSpan.FromSeconds(12)).ContinueWith(_ => Ui.Run(() => _ = CheckAsync()));
    }

    private string _announced;

    public async Task<Release> CheckAsync(bool quiet = false)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            string exe = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), "PeakPill.exe", StringComparison.OrdinalIgnoreCase))
                    exe = a.GetProperty("browser_download_url").GetString();
            var rel = new Release(v, tag, r.TryGetProperty("name", out var n) ? n.GetString() : tag,
                                  r.TryGetProperty("body", out var b) ? b.GetString() : "", exe, r.GetProperty("html_url").GetString());
            if (Normalize(v) <= Normalize(Current) || exe == null) return null;
            if (!quiet && _announced != tag)
            {
                _announced = tag;
                Ui.Run(() => Available?.Invoke(rel));
            }
            return rel;
        }
        catch { return null; } // offline, GitHub busy … next time
    }

    /// <summary>Shows the notice for a release found by hand (Settings › Info › Jetzt prüfen).</summary>
    public void Announce(Release rel) { _announced = rel.Tag; Ui.Run(() => Available?.Invoke(rel)); }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>
    /// Downloads the new exe and lets a tiny script swap it in after PeakPill has closed, then start it again.
    /// Returns false when the download failed (PeakPill keeps running).
    /// </summary>
    public static async Task<bool> InstallAsync(Release rel, IProgress<double> progress = null)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "PeakPill-update");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "PeakPill.exe");
            using (var resp = await Http.GetAsync(rel.ExeUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(file);
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            if (new FileInfo(file).Length < 100_000) return false; // not an exe – keep the old one

            var target = Environment.ProcessPath;
            var script = Path.Combine(dir, "swap.cmd");
            // waits for this process, copies the new exe over the old one (retries while Windows still holds it), starts it
            File.WriteAllText(script,
                "@echo off\r\n" +
                $"set PID={Environment.ProcessId}\r\n" +
                ":wait\r\n" +
                "tasklist /FI \"PID eq %PID%\" | find \"%PID%\" >nul && (timeout /t 1 >nul & goto wait)\r\n" +
                "set N=0\r\n" +
                ":copy\r\n" +
                $"copy /y \"{file}\" \"{target}\" >nul || (set /a N+=1 & timeout /t 1 >nul & if %N% LSS 20 goto copy)\r\n" +
                $"start \"\" \"{target}\"\r\n" +
                "del \"%~f0\"\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            return true;
        }
        catch (Exception ex) { App.Log(ex); return false; }
    }
}
