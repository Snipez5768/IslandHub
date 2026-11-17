using System.Windows;
using System.Windows.Controls;
using PeakPill.Core;
using PeakPill.Services;

namespace PeakPill.UI;

/// <summary>Settings › KI (2026-11-17): who answers, the user's own API key (encrypted) and the model.</summary>
public partial class SettingsWindow
{
    /// <summary>Until "Prüfen" has asked the service which models the key may use.</summary>
    private static readonly Dictionary<AiProvider, string[]> AiKnown = new()
    {
        [AiProvider.Claude] = new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-2" },
        [AiProvider.ChatGpt] = new[] { "gpt-5" },
        [AiProvider.Gemini] = new[] { "gemini-2.5-flash", "gemini-2.5-pro" },
    };
    private static readonly Dictionary<AiProvider, List<string>> AiFetched = new();
    private bool _aiLoading;

    // ---------- language (2026-11-17): English | Deutsch – switching restarts the island ----------

    private bool _langLoading;

    private void LoadLanguage()
    {
        _langLoading = true;
        LangEnglish.IsChecked = S.Language != "de";
        LangGerman.IsChecked = S.Language == "de";
        _langLoading = false;
    }

    private void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _langLoading) return;
        var lang = sender == LangGerman ? "de" : "en";
        if (lang == S.Language) return;
        S.Language = lang;
        App.SaveSettings();
        LangNote.Text = lang == "de" ? "PeakPill startet neu …" : "PeakPill is restarting …";
        Dispatcher.BeginInvoke(new Action(App.Restart), System.Windows.Threading.DispatcherPriority.Background);
    }

    private AiProvider AiChosen => AiChatGpt.IsChecked == true ? AiProvider.ChatGpt : AiGemini.IsChecked == true ? AiProvider.Gemini : AiProvider.Claude;

    private void LoadAi()
    {
        _aiLoading = true;
        AiClaude.IsChecked = S.AiProvider == AiProvider.Claude;
        AiChatGpt.IsChecked = S.AiProvider == AiProvider.ChatGpt;
        AiGemini.IsChecked = S.AiProvider == AiProvider.Gemini;
        _aiLoading = false;
        ShowAiProvider();
    }

    private void ShowAiProvider()
    {
        var p = S.AiProvider;
        _aiLoading = true;
        OptAiKey.Password = AiService.HasKey(p) ? SecretMask : "";
        AiKeyHint.Text = AiService.KeyHint(p);
        AiKeyLink.NavigateUri = new Uri(AiService.KeyUrl(p));
        AiKeyStatus.Text = AiService.HasKey(p) ? L.T("Gespeichert. „Prüfen“ testet ihn.") : L.T($"Noch kein Schlüssel für {AiService.Name(p)}.");
        FillAiModels(p);
        _aiLoading = false;
    }

    private void FillAiModels(AiProvider p)
    {
        _aiLoading = true;
        var cur = AiService.GetModel(p);
        var list = AiFetched.TryGetValue(p, out var f) && f.Count > 0 ? f.ToList() : AiKnown[p].ToList();
        if (!list.Contains(cur)) list.Insert(0, cur);
        OptAiModel.Items.Clear();
        foreach (var id in list)
        {
            var pretty = AiService.PrettyModel(id);
            var item = new ComboBoxItem { Content = pretty == id ? id : $"{pretty}  ·  {id}", Tag = id };
            OptAiModel.Items.Add(item);
            if (id == cur) OptAiModel.SelectedItem = item;
        }
        _aiLoading = false;
    }

    private void AiProvider_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _aiLoading) return;
        S.AiProvider = AiChosen;
        App.NotifySettingsChanged();
        ShowAiProvider();
    }

    private void AiKey_LostFocus(object sender, RoutedEventArgs e) => SaveAiKey();

    /// <summary>Saves what is in the field; an emptied field removes the key.</summary>
    private void SaveAiKey()
    {
        var p = S.AiProvider;
        var pw = OptAiKey.Password.Trim();
        if (pw == SecretMask) return;
        if (pw.Length == 0 && !AiService.HasKey(p)) return;
        AiService.SetKey(p, pw);
        AiFetched.Remove(p);
        App.NotifySettingsChanged();
        OptAiKey.Password = pw.Length > 0 ? SecretMask : "";
        AiKeyStatus.Text = pw.Length > 0 ? L.T("Gespeichert. „Prüfen“ testet ihn.") : L.T("Schlüssel entfernt.");
    }

    private async void AiCheck_Click(object sender, RoutedEventArgs e)
    {
        SaveAiKey();
        var p = S.AiProvider;
        var key = AiService.GetKey(p);
        if (key.Length == 0) { AiKeyStatus.Text = L.T("Trag zuerst einen Schlüssel ein."); return; }
        AiCheckBtn.IsEnabled = false;
        AiKeyStatus.Text = L.T("Prüfe …");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await AiService.ListModels(p, key, cts.Token);
            if (S.AiProvider != p) return;
            AiFetched[p] = models;
            var cur = AiService.GetModel(p);
            // the built-in default is not offered to this key (older account, other region …): take the first one it has
            if (models.Count > 0 && !models.Contains(cur) && cur == AiService.DefaultModel(p))
            {
                AiService.SetModel(p, models[0]);
                App.NotifySettingsChanged();
                cur = models[0];
            }
            FillAiModels(p);
            AiKeyStatus.Text = models.Count == 0 ? L.T("Schlüssel passt, aber es ist kein Chat-Modell freigeschaltet.")
                : models.Contains(cur) ? L.T($"Schlüssel passt · {models.Count} Modelle verfügbar.")
                : L.T($"Schlüssel passt · „{cur}“ ist nicht dabei, wähl eins aus der Liste.");
        }
        catch (OperationCanceledException) { AiKeyStatus.Text = L.T("Keine Antwort vom Dienst. Bist du online?"); }
        catch (Exception ex) { AiKeyStatus.Text = AiService.Explain(ex); }
        finally { AiCheckBtn.IsEnabled = true; }
    }

    private void AiModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _aiLoading || OptAiModel.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        AiService.SetModel(S.AiProvider, id);
        App.NotifySettingsChanged();
    }
}
