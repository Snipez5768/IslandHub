using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using PeakPill.Core;

namespace PeakPill.UI;

/// <summary>
/// Settings to a file and back (user wish 2026-11-17), e.g. for a second PC. Keys, secrets and passwords are never
/// written into the file (they are bound to this Windows account anyway) and are kept when loading.
/// </summary>
public partial class SettingsWindow
{
    private static readonly Regex Secret = new("Protected|Secret|Password|Token|^LocalSendId$", RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions BackupOpts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "PeakPill-Einstellungen.json", Filter = "JSON|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            App.SaveSettings();
            var node = JsonSerializer.SerializeToNode(S, BackupOpts) as JsonObject ?? new JsonObject();
            foreach (var k in node.Select(p => p.Key).Where(k => Secret.IsMatch(k)).ToList()) node.Remove(k);
            node["PeakPillBackup"] = 1;
            File.WriteAllText(dlg.FileName, node.ToJsonString(BackupOpts));
            BackupStatus.Text = L.T("Gesichert.");
        }
        catch (Exception ex) { App.Log(ex); BackupStatus.Text = ex.Message; }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(dlg.FileName)) is not JsonObject imported || imported["PeakPillBackup"] == null)
            {
                BackupStatus.Text = L.T("Das ist keine PeakPill-Sicherung.");
                return;
            }
            // today's settings, the file's values on top – the secrets stay as they are here
            var merged = JsonSerializer.SerializeToNode(S, BackupOpts) as JsonObject ?? new JsonObject();
            foreach (var (k, v) in imported)
                if (!Secret.IsMatch(k) && k != "PeakPillBackup") merged[k] = v?.DeepClone();
            var fresh = merged.Deserialize<AppSettings>(BackupOpts);
            if (fresh == null) return;
            foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanRead && p.CanWrite))
                p.SetValue(S, p.GetValue(fresh));
            App.SaveSettings();
            App.Restart(); // everything (language, tabs, keys) comes up fresh
        }
        catch (Exception ex) { App.Log(ex); BackupStatus.Text = L.T("Die Datei lässt sich nicht lesen."); }
    }
}
