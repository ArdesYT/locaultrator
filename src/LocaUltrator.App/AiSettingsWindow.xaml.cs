using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LocaUltrator.Core;

namespace LocaUltrator.App;

public partial class AiSettingsWindow : Window
{
    private static readonly string[] SupportedModels =
    [
        GeminiModelRouting.Flash37, GeminiModelRouting.Pro31
    ];
    private static readonly TierOption[] TierOptions = Enum.GetValues<GeminiUsageTier>()
        .Select(tier => new TierOption(tier, GeminiUsageProfiles.DisplayName(tier))).ToArray();
    private readonly ObservableCollection<GeminiApiKeyEntry> _keys;
    private GeminiApiKeyEntry? _editedKey;
    private bool _loadingEditor;
    private bool _testing;
    private bool _initialized;
    private CancellationTokenSource? _autoSaveDelay;

    public AiSettingsWindow()
    {
        InitializeComponent();
        _keys = new ObservableCollection<GeminiApiKeyEntry>(App.ApiKeys.Select(key => key.Clone()));
        foreach (GeminiApiKeyEntry key in _keys) key.IsActive = key.Enabled;
        KeyList.ItemsSource = _keys;
        KeyFilePathText.Text = App.KeyFileStore.FilePath;
        ModelBox.ItemsSource = SupportedModels.Contains(App.Settings.Model, StringComparer.OrdinalIgnoreCase)
            ? SupportedModels : new[] { App.Settings.Model }.Concat(SupportedModels).ToArray();
        ModelBox.SelectedItem = App.Settings.Model;
        BatchSizeBox.Text = App.Settings.BatchSize.ToString(CultureInfo.InvariantCulture);
        ThinkingLevelBox.ItemsSource = new[] { "high", "medium", "low" };
        ThinkingLevelBox.SelectedItem = string.IsNullOrWhiteSpace(App.Settings.ThinkingLevel) ? "high" : App.Settings.ThinkingLevel;
        UsageTierBox.ItemsSource = TierOptions;
        StyleGuideBox.Text = App.Settings.StyleGuide;
        GlossaryBox.Text = App.Settings.Glossary;
        if (_keys.Count > 0) KeyList.SelectedItem = _keys[0];
        else OnAddKey(this, new RoutedEventArgs());
        _initialized = true;
    }

    private void OnKeySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingEditor) return;
        CommitEditor(false);
        _editedKey = KeyList.SelectedItem as GeminiApiKeyEntry;
        LoadEditor();
    }

    private void LoadEditor()
    {
        _loadingEditor = true;
        try
        {
            KeyNameBox.Text = _editedKey?.Name ?? string.Empty;
            ApiKeyBox.Password = _editedKey?.Secret ?? string.Empty;
            KeyEnabledBox.IsChecked = _editedKey?.Enabled == true;
            UsageTierBox.SelectedItem = _editedKey is null ? TierOptions[0] : TierOptions.First(option => option.Tier == _editedKey.UsageTier);
            RequestsPerMinuteBox.Text = (_editedKey?.RequestsPerMinute ?? 5).ToString(CultureInfo.InvariantCulture);
            TokensPerMinuteBox.Text = (_editedKey?.TokensPerMinute ?? 25_000).ToString(CultureInfo.InvariantCulture);
            RequestsPerDayBox.Text = (_editedKey?.RequestsPerDay ?? 20).ToString(CultureInfo.InvariantCulture);
            ProRequestsPerMinuteBox.Text = (_editedKey?.ProRequestsPerMinute ?? 1).ToString(CultureInfo.InvariantCulture);
            ProTokensPerMinuteBox.Text = (_editedKey?.ProTokensPerMinute ?? 1).ToString(CultureInfo.InvariantCulture);
            ProRequestsPerDayBox.Text = (_editedKey?.ProRequestsPerDay ?? 1).ToString(CultureInfo.InvariantCulture);
            QuotaGroupBox.Text = _editedKey?.QuotaGroup ?? "Alapértelmezett projekt";
            ActiveBadge.Visibility = _editedKey?.Enabled == true ? Visibility.Visible : Visibility.Collapsed;
            bool enabled = _editedKey is not null;
            KeyNameBox.IsEnabled = ApiKeyBox.IsEnabled = KeyEnabledBox.IsEnabled = UsageTierBox.IsEnabled =
                RequestsPerMinuteBox.IsEnabled = TokensPerMinuteBox.IsEnabled = RequestsPerDayBox.IsEnabled =
                QuotaGroupBox.IsEnabled = enabled;
            bool proEnabled = enabled && _editedKey is not null && GeminiUsageProfiles.AllowsGemini31Pro(_editedKey.UsageTier);
            ProRequestsPerMinuteBox.IsEnabled = ProTokensPerMinuteBox.IsEnabled = ProRequestsPerDayBox.IsEnabled = proEnabled;
        }
        finally { _loadingEditor = false; }
    }

    private bool CommitEditor(bool showErrors)
    {
        if (_editedKey is null) return true;
        string name = KeyNameBox.Text.Trim();
        string secret = ApiKeyBox.Password.Trim();
        string quotaGroup = QuotaGroupBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(quotaGroup))
        {
            if (showErrors) MessageBox.Show("Minden kulcshoz adj nevet, Gemini API-kulcsot és projektcsoportot.",
                "Hiányos kulcs", MessageBoxButton.OK, MessageBoxImage.Warning);
            return !showErrors;
        }
        if (!int.TryParse(RequestsPerMinuteBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rpm) || rpm is < 1 or > 10_000)
        {
            if (showErrors) MessageBox.Show("Az RPM-korlát 1 és 10 000 közötti egész szám legyen.",
                "Hibás kvótabeállítás", MessageBoxButton.OK, MessageBoxImage.Warning);
            return !showErrors;
        }
        if (!int.TryParse(ProRequestsPerMinuteBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int proRpm) || proRpm is < 1 or > 10_000)
        {
            if (showErrors) MessageBox.Show("A Gemini 3.1 Pro RPM-korlát 1 és 10 000 közötti egész szám legyen.",
                "Hibás Pro-kvótabeállítás", MessageBoxButton.OK, MessageBoxImage.Warning);
            return !showErrors;
        }
        if (!int.TryParse(TokensPerMinuteBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tpm) || tpm is < 1 or > 2_000_000_000 ||
            !int.TryParse(ProTokensPerMinuteBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int proTpm) || proTpm is < 1 or > 2_000_000_000)
        {
            if (showErrors) MessageBox.Show("A TPM-limitek 1 és 2 000 000 000 közötti egész számok legyenek.",
                "Hibás TPM-beállítás", MessageBoxButton.OK, MessageBoxImage.Warning);
            return !showErrors;
        }
        if (!int.TryParse(RequestsPerDayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rpd) || rpd is < 1 or > 10_000_000 ||
            !int.TryParse(ProRequestsPerDayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int proRpd) || proRpd is < 1 or > 10_000_000)
        {
            if (showErrors) MessageBox.Show("Az RPD-limitek 1 és 10 000 000 közötti egész számok legyenek.",
                "Hibás RPD-beállítás", MessageBoxButton.OK, MessageBoxImage.Warning);
            return !showErrors;
        }

        if (!string.Equals(_editedKey.Secret, secret, StringComparison.Ordinal))
        {
            _editedKey.RuntimeState = GeminiKeyState.Unknown;
            _editedKey.RuntimeMessage = "A kulcs megváltozott; újratesztelés szükséges.";
            _editedKey.LastCheckedUtc = null;
        }
        _editedKey.Name = name;
        _editedKey.Secret = secret;
        _editedKey.Enabled = KeyEnabledBox.IsChecked == true;
        _editedKey.IsActive = _editedKey.Enabled;
        _editedKey.UsageTier = (UsageTierBox.SelectedItem as TierOption)?.Tier ?? GeminiUsageTier.Free;
        _editedKey.RequestsPerMinute = rpm;
        _editedKey.TokensPerMinute = tpm;
        _editedKey.RequestsPerDay = rpd;
        _editedKey.ProRequestsPerMinute = proRpm;
        _editedKey.ProTokensPerMinute = proTpm;
        _editedKey.ProRequestsPerDay = proRpd;
        _editedKey.QuotaGroup = quotaGroup;
        ActiveBadge.Visibility = _editedKey.Enabled ? Visibility.Visible : Visibility.Collapsed;
        KeyList.Items.Refresh();
        return true;
    }

    private void OnUsageTierChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingEditor || UsageTierBox.SelectedItem is not TierOption option) return;
        bool proEnabled = GeminiUsageProfiles.AllowsGemini31Pro(option.Tier);
        ProRequestsPerMinuteBox.IsEnabled = ProTokensPerMinuteBox.IsEnabled = ProRequestsPerDayBox.IsEnabled = proEnabled;
        if (proEnabled)
        {
            if (!int.TryParse(ProTokensPerMinuteBox.Text, out int proTpm) || proTpm <= 1)
                ProTokensPerMinuteBox.Text = GeminiUsageProfiles.SuggestedProTokensPerMinute(option.Tier).ToString(CultureInfo.InvariantCulture);
            if (!int.TryParse(ProRequestsPerDayBox.Text, out int proRpd) || proRpd <= 1)
                ProRequestsPerDayBox.Text = GeminiUsageProfiles.SuggestedProRequestsPerDay(option.Tier).ToString(CultureInfo.InvariantCulture);
        }
        if (!_initialized) return;
        CommitEditor(false);
        UpdateAppKeySnapshot();
        CancelDelayedSave();
        _ = PersistKeyFileAsync("Az előfizetési szint azonnal mentve. Az RPM/TPM/RPD értékeket az AI Studio aktív limitje szerint kezeld.");
    }

    private void OnKeyEditorChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _loadingEditor) return;
        CommitEditor(false);
        UpdateAppKeySnapshot();
        ScheduleKeyFileSave();
    }

    private void OnAddKey(object sender, RoutedEventArgs e)
    {
        CommitEditor(false);
        var key = new GeminiApiKeyEntry
        {
            Name = $"Gemini kulcs {_keys.Count + 1}", Enabled = true, IsActive = true,
            UsageTier = GeminiUsageTier.Free,
            RequestsPerMinute = GeminiUsageProfiles.SuggestedRequestsPerMinute(GeminiUsageTier.Free),
            TokensPerMinute = GeminiUsageProfiles.SuggestedTokensPerMinute(GeminiUsageTier.Free),
            RequestsPerDay = GeminiUsageProfiles.SuggestedRequestsPerDay(GeminiUsageTier.Free),
            ProRequestsPerMinute = GeminiUsageProfiles.SuggestedProRequestsPerMinute(GeminiUsageTier.Free),
            ProTokensPerMinute = GeminiUsageProfiles.SuggestedProTokensPerMinute(GeminiUsageTier.Free),
            ProRequestsPerDay = GeminiUsageProfiles.SuggestedProRequestsPerDay(GeminiUsageTier.Free),
            QuotaGroup = "Alapértelmezett projekt"
        };
        _keys.Add(key);
        KeyList.SelectedItem = key;
        ApiKeyBox.Focus();
    }

    private async void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        if (KeyList.SelectedItem is not GeminiApiKeyEntry key) return;
        int index = _keys.IndexOf(key);
        _keys.Remove(key);
        KeyList.SelectedItem = _keys.Count == 0 ? null : _keys[Math.Min(index, _keys.Count - 1)];
        KeyList.Items.Refresh();
        UpdateAppKeySnapshot();
        await PersistKeyFileAsync("A kulcs törlése mentve a kulcsfájlba.");
    }

    private void OnMoveKeyUp(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void OnMoveKeyDown(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int direction)
    {
        if (KeyList.SelectedItem is not GeminiApiKeyEntry key) return;
        CommitEditor(false);
        int oldIndex = _keys.IndexOf(key);
        int newIndex = Math.Clamp(oldIndex + direction, 0, _keys.Count - 1);
        if (oldIndex == newIndex) return;
        _keys.Move(oldIndex, newIndex);
        KeyList.SelectedItem = key;
        UpdateAppKeySnapshot();
        ScheduleKeyFileSave();
    }

    private bool TryReadSettings(out GeminiSettings settings)
    {
        settings = new GeminiSettings();
        if (!CommitEditor(true)) return false;
        if (_keys.Where(key => key.Enabled).GroupBy(key => key.Secret, StringComparer.Ordinal).Any(group => group.Count() > 1))
        { MessageBox.Show("Ugyanaz az API-kulcs többször szerepel az engedélyezett listában."); return false; }
        if (_keys.Count > 20) { MessageBox.Show("Legfeljebb 20 API-kulcs használható."); return false; }
        if (ModelBox.SelectedItem is not string model || string.IsNullOrWhiteSpace(model))
        { MessageBox.Show("Válassz Gemini modellt."); return false; }
        if (!int.TryParse(BatchSizeBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int batch) || batch is < 1 or > 100)
        { MessageBox.Show("A kötegméret 1 és 100 közötti egész szám legyen."); return false; }
        settings = new GeminiSettings
        {
            Model = model, BatchSize = batch, Temperature = App.Settings.Temperature,
            ThinkingLevel = ThinkingLevelBox.SelectedItem as string ?? "high",
            StyleGuide = StyleGuideBox.Text.Trim(), Glossary = GlossaryBox.Text.Trim(),
            RememberApiKey = true
        };
        return true;
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (_testing || _editedKey is null || !TryReadSettings(out GeminiSettings settings)) return;
        SetTesting(true);
        try { await TestKeyAsync(_editedKey, settings); }
        finally { SetTesting(false); }
    }

    private async void OnTestAll(object sender, RoutedEventArgs e)
    {
        if (_testing || !TryReadSettings(out GeminiSettings settings)) return;
        GeminiApiKeyEntry[] enabled = _keys.Where(key => key.Enabled).ToArray();
        if (enabled.Length == 0) { MessageBox.Show("Nincs engedélyezett kulcs."); return; }
        SetTesting(true);
        int succeeded = 0;
        try
        {
            foreach (GeminiApiKeyEntry key in enabled)
            {
                StatusText.Text = $"Kulcs tesztelése: {key.Name}… A kvótavédelem szükség esetén vár a következő küldés előtt.";
                StatusText.Foreground = Brush(Color.FromRgb(243, 248, 252));
                bool success = await TestKeyAsync(key, settings, updateSummary: false);
                if (success) succeeded++;
            }
            int failed = enabled.Length - succeeded;
            StatusText.Text = $"Kulcsteszt kész: {succeeded}/{enabled.Length} működik, {failed} hibás vagy átmenetileg nem elérhető. A részletekhez vidd az egeret a kulcs állapotjelzőjére.";
            StatusText.Foreground = Brush(failed == 0 ? Color.FromRgb(91, 231, 196) : Color.FromRgb(246, 200, 95));
        }
        finally { SetTesting(false); }
    }

    private async Task<bool> TestKeyAsync(GeminiApiKeyEntry key, GeminiSettings settings, bool updateSummary = true)
    {
        key.RuntimeState = GeminiKeyState.Testing;
        key.RuntimeMessage = "Kapcsolat ellenőrzése folyamatban…";
        KeyList.Items.Refresh();
        if (updateSummary)
        {
            StatusText.Text = $"Kapcsolódás: {key.Name}…";
            StatusText.Foreground = Brush(Color.FromRgb(243, 248, 252));
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(190));
            GeminiConnectionTestResult test = await App.Gemini.TestConnectionDetailedAsync(
                EffectivePacedKey(key), settings, timeout.Token);
            key.RuntimeState = GeminiKeyState.Ready;
            key.RuntimeMessage = test.Message;
            key.LastCheckedUtc = DateTime.UtcNow;
            if (updateSummary)
            {
                StatusText.Text = $"Kapcsolat rendben: {key.Name}. {test.Message}";
                StatusText.Foreground = Brush(test.GenerationSucceeded
                    ? Color.FromRgb(91, 231, 196) : Color.FromRgb(246, 200, 95));
            }
            return true;
        }
        catch (Exception ex)
        {
            GeminiFailureInfo diagnosis = GeminiTranslationService.Diagnose(ex);
            bool notAKeyFailure = diagnosis.State is GeminiKeyState.ServerBusy or GeminiKeyState.ServerError or
                GeminiKeyState.Timeout or GeminiKeyState.RateLimited;
            key.RuntimeState = notAKeyFailure ? GeminiKeyState.Ready : diagnosis.State;
            key.RuntimeMessage = Redact(diagnosis.UserMessage);
            key.LastCheckedUtc = DateTime.UtcNow;
            if (updateSummary)
            {
                StatusText.Text = notAKeyFailure
                    ? $"A kulcs érvényesnek tekinthető; átmeneti Google-szolgáltatás vagy modellkvóta: {key.RuntimeMessage}"
                    : $"Kapcsolati hiba ({key.Name}): {key.RuntimeMessage}";
                StatusText.Foreground = Brush(notAKeyFailure
                    ? Color.FromRgb(246, 200, 95) : Color.FromRgb(251, 113, 133));
            }
            return notAKeyFailure;
        }
        finally { KeyList.Items.Refresh(); }
    }

    private void SetTesting(bool value)
    {
        _testing = value;
        TestSelectedButton.IsEnabled = TestAllButton.IsEnabled = SaveButton.IsEnabled = !value;
    }

    private GeminiApiKeyEntry EffectivePacedKey(GeminiApiKeyEntry key)
    {
        GeminiApiKeyEntry paced = key.Clone();
        paced.RequestsPerMinute = _keys.Where(other => other.Enabled &&
                string.Equals(other.QuotaGroup, key.QuotaGroup, StringComparison.OrdinalIgnoreCase))
            .Select(other => other.RequestsPerMinute).DefaultIfEmpty(key.RequestsPerMinute).Min();
        paced.TokensPerMinute = _keys.Where(other => other.Enabled &&
                string.Equals(other.QuotaGroup, key.QuotaGroup, StringComparison.OrdinalIgnoreCase))
            .Select(other => other.TokensPerMinute).DefaultIfEmpty(key.TokensPerMinute).Min();
        paced.RequestsPerDay = _keys.Where(other => other.Enabled &&
                string.Equals(other.QuotaGroup, key.QuotaGroup, StringComparison.OrdinalIgnoreCase))
            .Select(other => other.RequestsPerDay).DefaultIfEmpty(key.RequestsPerDay).Min();
        paced.ProRequestsPerMinute = _keys.Where(other => other.Enabled &&
                string.Equals(other.QuotaGroup, key.QuotaGroup, StringComparison.OrdinalIgnoreCase))
            .Select(other => other.ProRequestsPerMinute).DefaultIfEmpty(key.ProRequestsPerMinute).Min();
        paced.ProTokensPerMinute = _keys.Where(other => other.Enabled &&
                string.Equals(other.QuotaGroup, key.QuotaGroup, StringComparison.OrdinalIgnoreCase))
            .Select(other => other.ProTokensPerMinute).DefaultIfEmpty(key.ProTokensPerMinute).Min();
        paced.ProRequestsPerDay = _keys.Where(other => other.Enabled &&
                string.Equals(other.QuotaGroup, key.QuotaGroup, StringComparison.OrdinalIgnoreCase))
            .Select(other => other.ProRequestsPerDay).DefaultIfEmpty(key.ProRequestsPerDay).Min();
        return paced;
    }

    private void OnOpenRateLimits(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://aistudio.google.com/rate-limit?timeRange=last-28-days")
            { UseShellExecute = true });
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_testing || !TryReadSettings(out GeminiSettings settings)) return;
        foreach (GeminiApiKeyEntry key in _keys) key.IsActive = key.Enabled;
        UpdateAppKeySnapshot();
        App.Settings = settings;
        CancelDelayedSave();
        await PersistKeyFileAsync();
        await App.SettingsStore.SaveAsync(settings);
        DialogResult = true;
    }

    private async void OnOpenKeyFile(object sender, RoutedEventArgs e)
    {
        if (!CommitEditor(true)) return;
        UpdateAppKeySnapshot();
        CancelDelayedSave();
        try
        {
            await PersistKeyFileAsync();
            Process.Start(new ProcessStartInfo(App.KeyFileStore.FilePath) { UseShellExecute = true });
            StatusText.Text = "A kulcsfájl megnyílt. Kézi módosítás és mentés után nyomd meg az „Újratöltés fájlból” gombot.";
            StatusText.Foreground = Brush(Color.FromRgb(91, 231, 196));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Kulcsfájl megnyitása", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnReloadKeyFile(object sender, RoutedEventArgs e)
    {
        CancelDelayedSave();
        try
        {
            IReadOnlyList<GeminiApiKeyEntry> loaded = await App.KeyFileStore.LoadAsync();
            _loadingEditor = true;
            try
            {
                _editedKey = null;
                KeyList.SelectedItem = null;
                _keys.Clear();
                foreach (GeminiApiKeyEntry key in loaded) _keys.Add(key.Clone());
            }
            finally { _loadingEditor = false; }
            if (_keys.Count > 0) KeyList.SelectedItem = _keys[0];
            else OnAddKey(this, new RoutedEventArgs());
            UpdateAppKeySnapshot();
            MirrorToWindowsCredentialStore();
            StatusText.Text = $"Kulcsfájl újratöltve: {App.ApiKeys.Count} kulcs. A kézi módosítások érvénybe léptek.";
            StatusText.Foreground = Brush(Color.FromRgb(91, 231, 196));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message + "\n\nA meglévő kulcslista nem változott.",
                "Hibás kulcsfájl", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateAppKeySnapshot()
    {
        App.ApiKeys = _keys.Where(key => !string.IsNullOrWhiteSpace(key.Secret))
            .Select(key => key.Clone()).ToList();
    }

    private void ScheduleKeyFileSave()
    {
        CancelDelayedSave();
        var delay = new CancellationTokenSource();
        _autoSaveDelay = delay;
        _ = SaveAfterDelayAsync(delay);
    }

    private async Task SaveAfterDelayAsync(CancellationTokenSource delay)
    {
        try
        {
            await Task.Delay(200, delay.Token);
            await PersistKeyFileAsync("Kulcsmódosítás automatikusan mentve.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "A kulcsfájl automatikus mentése sikertelen: " + ex.Message;
            StatusText.Foreground = Brush(Color.FromRgb(251, 113, 133));
        }
        finally
        {
            if (ReferenceEquals(_autoSaveDelay, delay)) _autoSaveDelay = null;
            delay.Dispose();
        }
    }

    private async Task PersistKeyFileAsync(string? successMessage = null)
    {
        GeminiApiKeyEntry[] snapshot = App.ApiKeys.Select(key => key.Clone()).ToArray();
        await App.KeyFileStore.SaveAsync(snapshot);
        MirrorToWindowsCredentialStore();
        if (!string.IsNullOrWhiteSpace(successMessage) && !_testing)
        {
            StatusText.Text = successMessage + "  Fájl: " + App.KeyFileStore.FilePath;
            StatusText.Foreground = Brush(Color.FromRgb(91, 231, 196));
        }
    }

    private static void MirrorToWindowsCredentialStore()
    {
        try
        {
            if (App.ApiKeys.Count == 0) GeminiCredentialStore.DeleteAll();
            else GeminiCredentialStore.WriteAll(App.ApiKeys);
        }
        catch
        {
            // A kézzel menthető JSON az elsődleges tároló; ennek sikere nem függ a Windows biztonsági másolattól.
        }
    }

    private void CancelDelayedSave()
    {
        _autoSaveDelay?.Cancel();
        _autoSaveDelay = null;
    }

    private string Redact(string message)
    {
        string safe = message;
        foreach (string secret in _keys.Select(key => key.Secret).Where(secret => !string.IsNullOrWhiteSpace(secret)))
            safe = safe.Replace(secret, "[API-KULCS]", StringComparison.Ordinal);
        return safe;
    }

    private static SolidColorBrush Brush(Color color) => new(color);
    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_testing) return;
        CommitEditor(false);
        UpdateAppKeySnapshot();
        CancelDelayedSave();
        try { await PersistKeyFileAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "A kulcsfájl mentése sikertelen", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        DialogResult = false;
    }
    private sealed record TierOption(GeminiUsageTier Tier, string Label) { public override string ToString() => Label; }
}
