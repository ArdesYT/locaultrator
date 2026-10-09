using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LocaUltrator.Core;
using Microsoft.Win32;

namespace LocaUltrator.App;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ProjectStore _store = new();
    private readonly SourceImporter _importer = new();
    private readonly TokenProtectionService _protection = new();
    private readonly ClipboardTranslationService _clipboard;
    private readonly System.Windows.Threading.DispatcherTimer _autosave;
    private readonly System.Windows.Threading.DispatcherTimer _clipboardMonitor;
    private ProjectDocument? _project;
    private ICollectionView? _unitsView;
    private TranslationUnit? _selectedUnit;
    private string _searchText = string.Empty;
    private string _statusFilter = "Aktív szövegek";
    private bool _isBusy;
    private string _busyMessage = string.Empty;
    private CancellationTokenSource? _busyCancellation;
    private uint _lastClipboardSequence;
    private bool _clipboardProcessing;
    private bool _suppressUnitRefresh;
    private bool _dirty;
    private int _completedCount, _pendingCount, _waitingCount, _reviewCount, _totalCount;

    public MainViewModel()
    {
        _clipboard = new ClipboardTranslationService(_protection);
        NewProjectCommand = new AsyncCommand(NewProjectAsync, () => !IsBusy);
        OpenProjectCommand = new AsyncCommand(OpenProjectAsync, () => !IsBusy);
        SaveCommand = new AsyncCommand(SaveAsync, () => Project is not null && !IsBusy);
        ImportUpdateCommand = new AsyncCommand(ImportUpdateAsync, () => Project is not null && !IsBusy);
        ExportCommand = new AsyncCommand(ExportAsync, () => Project is not null && !IsBusy);
        TranslateCommand = new AsyncCommand(StartTranslationAsync, () => Project is not null && !IsBusy);
        CopyBatchCommand = new AsyncCommand(CopyBatchAsync, () => Project is not null && !IsBusy);
        RecopyBatchCommand = new RelayCommand(RecopyBatch, () => Project?.PendingBatches.Count > 0 && !IsBusy);
        ProcessResponseCommand = new AsyncCommand(ProcessResponseAsync, () => Project?.PendingBatches.Count > 0 && !IsBusy);
        CancelBatchCommand = new AsyncCommand(CancelBatchAsync, () => Project?.PendingBatches.Count > 0 && !IsBusy);
        TokenPromptCommand = new RelayCommand(OpenTokenPrompt, () => Project is not null && !IsBusy);
        ProcessTokenProfileCommand = new AsyncCommand(ProcessTokenProfileAsync, () => Project is not null && !IsBusy);
        CancelBusyCommand = new RelayCommand(CancelBusy, () => CanCancelBusy);
        ProjectSettingsCommand = new AsyncCommand(OpenProjectSettingsAsync, () => !IsBusy);
        ToggleSkipCommand = new RelayCommand(ToggleSkip, () => SelectedUnit is not null && !IsBusy);
        HelpCommand = new RelayCommand(ShowHelp);
        _autosave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _autosave.Tick += async (_, _) =>
        {
            if (!_dirty || Project is null || IsBusy) return;
            try { await _store.SaveAsync(Project); _dirty = false; AddLog("Automatikus mentés kész."); }
            catch (Exception ex) { AddLog("Automatikus mentési hiba: " + ex.Message); }
        };
        _autosave.Start();
        _clipboardMonitor = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _clipboardMonitor.Tick += async (_, _) => await ObserveClipboardAsync();
        _clipboardMonitor.Start();
        AddLog("LocaUltrator 0.8.7 elindult · 3.7 Flash / 3.1 Pro dinamikus modellútvonal · RPM/TPM/RPD védelem.");
    }

    public ProjectDocument? Project { get => _project; private set { _project = value; Raise(); RaiseHeader(); } }
    public ICollectionView? UnitsView { get => _unitsView; private set { _unitsView = value; Raise(); } }
    public TranslationUnit? SelectedUnit { get => _selectedUnit; set { _selectedUnit = value; Raise(); RefreshCommands(); } }
    public ObservableCollection<string> Logs { get; } = [];
    public IReadOnlyList<string> StatusFilters { get; } = ["Aktív szövegek", "Fordítandó", "Válaszra vár", "Kész", "Ellenőrzendő", "Kihagyva", "Archivált"];
    public string SearchText { get => _searchText; set { _searchText = value ?? string.Empty; Raise(); UnitsView?.Refresh(); } }
    public string StatusFilter { get => _statusFilter; set { _statusFilter = value; Raise(); UnitsView?.Refresh(); } }
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Raise(); RefreshCommands(); } }
    public string BusyMessage { get => _busyMessage; private set { _busyMessage = value; Raise(); } }
    public bool CanCancelBusy => _busyCancellation is { IsCancellationRequested: false };
    public string ProjectName => Project?.Name ?? "Nincs megnyitott projekt";
    public string ProjectPath => Project?.ProjectFilePath ?? "Új projekt létrehozása vagy megnyitása szükséges";
    public string TokenProfileLabel => Project?.ActiveTokenProfile is not { } profile
        ? "Tokenprofil nincs aktiválva"
        : $"{profile.ProfileName} · {profile.Rules.Count + profile.Literals.Count} elem";
    public string SourceVersionLabel => string.IsNullOrWhiteSpace(Project?.SourceVersion) ? "Forrásverzió: nincs megadva" : "Forrásverzió: " + Project.SourceVersion;
    public string GeminiStatusLabel
    {
        get
        {
            IReadOnlyList<GeminiApiKeyEntry> keys = App.OrderedEnabledApiKeys();
            return keys.Count == 0 ? "NINCS API-KULCS · VÁGÓLAP KÉSZ" : $"{keys.Count} KULCS · SOROS KÖRFORGÁS";
        }
    }
    public IReadOnlyList<string> TranslationModes { get; } = ["Automatikus: API → vágólap", "Gemini API", "Vágólap"];
    public string SelectedTranslationMode
    {
        get => Project?.TranslationTransport switch
        {
            TranslationTransport.GeminiApi => TranslationModes[1],
            TranslationTransport.Clipboard => TranslationModes[2],
            _ => TranslationModes[0]
        };
        set
        {
            if (Project is null) return;
            TranslationTransport mode = value == TranslationModes[1] ? TranslationTransport.GeminiApi
                : value == TranslationModes[2] ? TranslationTransport.Clipboard : TranslationTransport.Automatic;
            if (Project.TranslationTransport == mode) return;
            Project.TranslationTransport = mode;
            _dirty = true;
            Raise(); Raise(nameof(PrimaryTranslationLabel)); Raise(nameof(TransportStatusLabel));
            _ = SaveTransportSelectionAsync(Project, mode);
        }
    }
    public string PrimaryTranslationLabel => Project?.TranslationTransport switch
    {
        TranslationTransport.GeminiApi => "Fordítás Gemini API-val",
        TranslationTransport.Clipboard => "Következő csomag a vágólapra",
        _ => "Automatikus fordítás indítása"
    };
    public string TransportStatusLabel => Project?.TranslationTransport switch
    {
        TranslationTransport.GeminiApi => "Kizárólag API · kulcshibánál megáll",
        TranslationTransport.Clipboard => $"Vágólap · {PendingPackageCount} aktív csomag",
        _ => App.OrderedEnabledApiKeys().Count == 0 ? "Automatikus · nincs kulcs, ezért vágólap" : "Automatikus · API-kulcsváltás, majd vágólap",
    };
    public int CompletedCount { get => _completedCount; private set { _completedCount = value; Raise(); } }
    public int PendingCount { get => _pendingCount; private set { _pendingCount = value; Raise(); } }
    public int WaitingCount { get => _waitingCount; private set { _waitingCount = value; Raise(); } }
    public int ReviewCount { get => _reviewCount; private set { _reviewCount = value; Raise(); } }
    public int TotalCount { get => _totalCount; private set { _totalCount = value; Raise(); } }
    public string ProgressLabel => TotalCount == 0 ? "0,0%" : $"{CompletedCount * 100d / TotalCount:0.0}%";
    public double ProgressValue => TotalCount == 0 ? 0 : CompletedCount * 100d / TotalCount;
    public int PendingPackageCount => Project?.PendingBatches.Count ?? 0;
    public bool ShouldShowStatusHud => Project?.ShowStatusHud == true;
    public bool ShouldUseGlobalClipboardHotkey => Project?.EnableGlobalClipboardHotkey == true && Project.ClipboardHotkey != "Kikapcsolva";
    public string ConfiguredClipboardHotkey => Project?.ClipboardHotkey ?? "Kikapcsolva";
    public event Action<HudStatus>? HudStatusChanged;
    public event Action? GlobalHotkeyConfigurationChanged;
    public event Action? HudVisibilitySettingsChanged;

    public AsyncCommand NewProjectCommand { get; }
    public AsyncCommand OpenProjectCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ImportUpdateCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public AsyncCommand TranslateCommand { get; }
    public AsyncCommand CopyBatchCommand { get; }
    public RelayCommand RecopyBatchCommand { get; }
    public AsyncCommand ProcessResponseCommand { get; }
    public AsyncCommand CancelBatchCommand { get; }
    public RelayCommand TokenPromptCommand { get; }
    public AsyncCommand ProcessTokenProfileCommand { get; }
    public RelayCommand CancelBusyCommand { get; }
    public AsyncCommand ProjectSettingsCommand { get; }
    public RelayCommand ToggleSkipCommand { get; }
    public RelayCommand HelpCommand { get; }

    public void RefreshGeminiStatus()
    {
        Raise(nameof(GeminiStatusLabel));
        Raise(nameof(TransportStatusLabel));
        Raise(nameof(PrimaryTranslationLabel));
    }

    private async Task SaveTransportSelectionAsync(ProjectDocument project, TranslationTransport mode)
    {
        if (string.IsNullOrWhiteSpace(project.ProjectFilePath)) return;
        try
        {
            await _store.SaveAsync(project);
            if (ReferenceEquals(Project, project) && project.TranslationTransport == mode) _dirty = false;
            AddLog($"Fordítási mód azonnal mentve: {SelectedTranslationMode}.");
        }
        catch (Exception ex) { AddLog("A fordítási mód mentése sikertelen: " + ex.Message); }
    }

    private async Task NewProjectAsync()
    {
        var sourceDialog = new NewLocalizationWindow { Owner = Application.Current.MainWindow };
        if (sourceDialog.ShowDialog() != true) return;
        if (sourceDialog.Source == NewLocalizationSource.GameFolder)
        {
            var gameWindow = new GameIntegrationWindow { Owner = Application.Current.MainWindow };
            if (gameWindow.ShowDialog() == true)
                await UseGameFolderAsync(gameWindow.SelectedImportFiles, gameWindow.GameRoot, gameWindow.DetectedEngine);
            return;
        }
        string[]? paths = SelectSourceFiles("Fordítási források kiválasztása");
        if (paths is null) return;
        string suggestion = paths.Length == 1 ? Path.GetFileNameWithoutExtension(paths[0]) : "Új fordítás";
        var nameDialog = new InputDialog("Új projekt", "Projekt neve", suggestion) { Owner = Application.Current.MainWindow };
        if (nameDialog.ShowDialog() != true) return;
        await RunBusyAsync("Forrásszövegek beolvasása…", async () =>
        {
            ImportedSource imported = await _importer.ImportAsync(paths, new Progress<string>(value => BusyMessage = value),
                sourceLanguage: "angol", targetLanguage: "magyar");
            if (!ConfirmDetectedTextLayouts(imported.SourceDocuments)) return;
            var project = new ProjectDocument { Name = nameDialog.Value, ProjectFilePath = LocaPaths.CreateProjectPath(nameDialog.Value) };
            SourceImporter.Merge(project, imported, false);
            BuiltInTokenProfiles.EnsureApplied(project, _protection);
            await _store.SaveAsync(project);
            SetProject(project);
            AddLog($"Projekt létrehozva: {project.Units.Count(unit => unit.Active):N0} aktív szöveg.");
        });
    }

    private async Task OpenProjectAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "LocaUltrator projekt megnyitása",
            Filter = "LocaUltrator projekt (project.locaultrator.json)|project.locaultrator.json|JSON fájl (*.json)|*.json",
            InitialDirectory = LocaPaths.Projects
        };
        if (dialog.ShowDialog() != true) return;
        await RunBusyAsync("Projekt betöltése…", async () =>
        {
            ProjectDocument project = await _store.LoadAsync(dialog.FileName);
            BuiltInTokenProfiles.EnsureApplied(project, _protection);
            SetProject(project);
            AddLog("Projekt megnyitva: " + project.Name);
        });
    }

    private async Task ImportUpdateAsync()
    {
        if (Project is null) return;
        string[]? paths = SelectSourceFiles("Projektfrissítés forrásainak kiválasztása");
        if (paths is null) return;
        await RunBusyAsync("Projektfrissítés összehasonlítása…", async () =>
        {
            ImportedSource imported = await _importer.ImportAsync(paths, new Progress<string>(value => BusyMessage = value),
                sourceLanguage: Project.SourceLanguage, targetLanguage: Project.TargetLanguage);
            ProjectDocument preview = await _store.LoadAsync(Project.ProjectFilePath);
            UpdateSummary summary = SourceImporter.Merge(preview, imported, true, installSourceDocuments: false);
            if (MessageBox.Show($"Új: {summary.New:N0}\nMódosult: {summary.Changed:N0}\nArchiválandó: {summary.Removed:N0}\nÜtközés: {summary.Conflicts:N0}\n\nAlkalmazzam a frissítést?",
                "Projektfrissítés előnézete", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await _store.SaveAsync(Project);
            File.Copy(Project.ProjectFilePath, Project.ProjectFilePath + $".backup-{DateTime.Now:yyyyMMdd-HHmmssfff}", false);
            summary = SourceImporter.Merge(Project, imported, true);
            BuiltInTokenProfiles.EnsureApplied(Project, _protection);
            await _store.SaveAsync(Project); _dirty = false;
            SetProject(Project);
            AddLog($"Forrás frissítve: új {summary.New}, módosult {summary.Changed}, archivált {summary.Removed}.");
        });
    }

    private async Task StartTranslationAsync()
    {
        if (Project is null) return;
        if (Project.TranslationTransport == TranslationTransport.Clipboard)
        {
            await CopyBatchAsync();
            return;
        }

        IReadOnlyList<GeminiApiKeyEntry> keys = App.OrderedEnabledApiKeys();
        if (keys.Count == 0)
        {
            if (Project.TranslationTransport == TranslationTransport.Automatic)
            {
                AddLog("Nincs engedélyezett API-kulcs; automatikus váltás vágólapos fordításra.");
                await CopyBatchAsync();
            }
            else MessageBox.Show("Előbb adj meg legalább egy engedélyezett Gemini API-kulcsot.",
                "Gemini API", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await TranslateApiAsync(Project.TranslationTransport == TranslationTransport.Automatic, keys);
    }

    private async Task TranslateApiAsync(bool fallbackToClipboard, IReadOnlyList<GeminiApiKeyEntry> keys)
    {
        if (Project is null) return;
        _busyCancellation = new CancellationTokenSource();
        Raise(nameof(CanCancelBusy)); CancelBusyCommand.Raise();
        bool shouldCreateClipboardBatch = false;
        try
        {
            await RunBusyAsync($"Gemini fordítás indítása · {keys[0].Name}…", async () =>
            {
                var progress = new Progress<TranslationProgress>(value => BusyMessage = value.Message);
                TranslationRunResult result = await App.Gemini.TranslateProjectAsync(Project, App.Settings, keys, false,
                    progress, _busyCancellation.Token);
                App.ApplyKeyRuntimeStatus(result);
                await _store.SaveAsync(Project); _dirty = false;
                RefreshStats(); UnitsView?.Refresh();
                AddLog($"Gemini kész: {result.Applied:N0} alkalmazva, {result.Rejected:N0} elutasítva, {result.Failed:N0} hiba, {result.KeySwitches:N0} kulcsváltás, utolsó útvonal: {result.LastModel} / {result.LastKeyName}.");
                string usage = string.Join(", ", keys.Where(key => result.KeyUsage.GetValueOrDefault(key.Id) > 0)
                    .Select(key => $"{key.Name}: {result.KeyUsage[key.Id]} köteg"));
                if (!string.IsNullOrWhiteSpace(usage)) AddLog("Kulcskörforgás: " + usage + ".");
                foreach (GeminiApiKeyEntry key in keys.Where(key => result.KeyMessages.ContainsKey(key.Id) && result.KeyStates.GetValueOrDefault(key.Id) != GeminiKeyState.Ready))
                    AddLog($"Kulcsállapot · {key.Name}: {result.KeyMessages[key.Id]}");
                if (result.ApiUnavailable && fallbackToClipboard)
                {
                    shouldCreateClipboardBatch = true;
                    AddLog("Minden API-kulcs sikertelen; automatikus váltás a mentett vágólapos munkafolyamatra.");
                    return;
                }
                if (result.Rejected + result.Failed > 0)
                    MessageBox.Show($"Elkészült: {result.Applied:N0}\nEllenőrzendő: {result.Rejected + result.Failed:N0}\n\n" + string.Join("\n", result.Errors.Take(8)),
                        "Gemini fordítás", MessageBoxButton.OK, MessageBoxImage.Warning);
                else if (result.ApiUnavailable)
                    MessageBox.Show(string.Join("\n", result.Errors.Take(8)), "Minden API-kulcs sikertelen",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }
        finally
        {
            _busyCancellation.Dispose(); _busyCancellation = null;
            Raise(nameof(CanCancelBusy)); CancelBusyCommand.Raise();
        }
        if (shouldCreateClipboardBatch) await CopyBatchAsync();
    }

    private async Task SaveAsync()
    {
        if (Project is null) return;
        await _store.SaveAsync(Project); _dirty = false; AddLog("Projekt mentve.");
    }

    private async Task ExportAsync()
    {
        if (Project is null) return;
        QualityReport report = RunQualityAudit();
        int incomplete = Project.Units.Count(unit => unit.Active && unit.Status is UnitStatus.Pending or UnitStatus.Waiting or UnitStatus.NeedsReview);
        if (incomplete > 0 || report.ErrorCount > 0)
        {
            MessageBox.Show($"Az export előtt még {incomplete:N0} sor nem kész, és {report.ErrorCount:N0} minőségi hiba maradt.",
                "Az export leállítva", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await RunBusyAsync("Magyar fájlok készítése…", async () =>
        {
            string path = await _store.ExportAsync(Project, _protection);
            await _store.SaveAsync(Project); _dirty = false;
            AddLog("Export elkészült: " + path);
            MessageBox.Show(path, "Export elkészült", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void OpenTokenPrompt()
    {
        if (Project is null) return;
        string Fill(string template) => template.Replace("{{PROJECT_NAME}}", Project.Name, StringComparison.Ordinal)
            .Replace("{{TOKEN_CANDIDATES_JSON}}", _protection.BuildCandidateReport(Project), StringComparison.Ordinal)
            .Replace("{{OUTPUT_SCHEMA}}", DefaultPrompts.OutputSchema, StringComparison.Ordinal);
        string template = string.IsNullOrWhiteSpace(Project.TokenPromptTemplate)
            ? DefaultPrompts.TokenProfilePrompt
            : Project.TokenPromptTemplate;
        var window = new PromptWindow(template, Fill, filledPrompt => SaveTokenPromptBesideSources(filledPrompt))
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() == true)
        {
            Project.TokenPromptTemplate = window.TemplateText;
            _dirty = true;
            AddLog("A projekt tokenvédelmi promptsablonja mentve.");
        }
    }

    private void SaveTokenPromptBesideSources(string filledPrompt)
    {
        if (Project is null) return;
        string projectFolder = Path.GetDirectoryName(Project.ProjectFilePath)
            ?? throw new InvalidOperationException("A projektnek nincs saját mappája.");
        string sourceFolder = Path.Combine(projectFolder, "Forrás-pillanatképek");
        Directory.CreateDirectory(sourceFolder);
        string promptPath = Path.Combine(sourceFolder, "TOKENMASZKOLASI_PROMPT.txt");
        File.WriteAllText(promptPath, filledPrompt, new UTF8Encoding(false));
        AddLog("A kitöltött tokenprompt a vágólapra és a forrásfájlok mellé került: " + promptPath);
    }

    private async Task ProcessTokenProfileAsync()
    {
        if (Project is null) return;
        string json = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            MessageBox.Show("A vágólap nem tartalmaz AI által készített tokenprofil-JSON-t.",
                "Tokenprofil", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busyCancellation = new CancellationTokenSource();
        Raise(nameof(CanCancelBusy));
        CancelBusyCommand.Raise();
        try
        {
            await RunBusyAsync("AI-tokenprofil biztonsági ellenőrzése…", async () =>
            {
                TokenProfile profile = _protection.ParseProfile(json);
                var progress = new Progress<string>(message => BusyMessage = message);
                TokenProfileValidation validation = await Task.Run(
                    () => _protection.ValidateProfile(profile, Project.Units, progress, _busyCancellation.Token),
                    _busyCancellation.Token);
                if (!validation.Success)
                    throw new InvalidDataException("A tokenprofil nem aktiválható:\n\n" + string.Join("\n", validation.Errors.Take(30)));

                _busyCancellation.Token.ThrowIfCancellationRequested();
                var preview = new TokenProfilePreviewWindow(profile, validation) { Owner = Application.Current.MainWindow };
                if (preview.ShowDialog() != true)
                {
                    AddLog("Az ellenőrzött AI-tokenprofil nem lett aktiválva.");
                    return;
                }

                await ActivateProfileCoreAsync(profile, progress, _busyCancellation.Token);
                BusyMessage = "Projekt mentése…";
                await _store.SaveAsync(Project, cancellationToken: _busyCancellation.Token);
                _dirty = false;
                AddLog($"Projekt-tokenprofil aktiválva: {profile.ProfileName}, {validation.MatchCount:N0} védett előfordulás.");
            });
        }
        finally
        {
            _busyCancellation.Dispose();
            _busyCancellation = null;
            Raise(nameof(CanCancelBusy));
            CancelBusyCommand.Raise();
        }
    }

    private async Task ActivateProfileCoreAsync(TokenProfile profile, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (Project is null) return;
        TokenProfile? oldProfile = Project.ActiveTokenProfile;
        int total = Project.Units.Count;
        const int chunkSize = 4096;
        for (int offset = 0; offset < total; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(chunkSize, total - offset);
            List<TranslationUnit> chunk = Project.Units.GetRange(offset, count);
            var updates = await Task.Run(() => chunk.Select(unit =>
            {
                MaskResult oldSource = _protection.Mask(unit.SourceText, oldProfile);
                string rawTranslation = oldProfile is null
                    ? unit.TranslationMasked
                    : _protection.Restore(unit.TranslationMasked, oldSource.Tokens);
                MaskResult newSource = _protection.Mask(unit.SourceText, profile);
                string newTranslation = _protection.MaskExistingTranslation(rawTranslation, profile, newSource.Tokens);
                bool valid = string.IsNullOrWhiteSpace(newTranslation) ||
                    (_protection.ValidateMarkers(newSource.Text, newTranslation, out _) &&
                     TextStructureGuard.Validate(newSource.Text, newTranslation, out _));
                string maskedContext = string.IsNullOrEmpty(unit.Context)
                    ? string.Empty
                    : _protection.Mask(unit.Context, profile).Text;
                return (Unit: unit, Source: newSource, Translation: newTranslation, MaskedContext: maskedContext, Valid: valid);
            }).ToArray(), cancellationToken);

            foreach (var update in updates)
            {
                string oldSignature = update.Unit.TokenSignature;
                update.Unit.MaskedSource = update.Source.Text;
                update.Unit.MaskedContext = update.MaskedContext;
                update.Unit.TokenSignature = update.Source.Signature;
                update.Unit.TranslationMasked = update.Translation;
                if (!update.Valid || (!string.IsNullOrWhiteSpace(oldSignature) &&
                    oldSignature != update.Source.Signature && update.Unit.Status == UnitStatus.Completed))
                    update.Unit.Status = UnitStatus.NeedsReview;
            }
            progress?.Report($"Tokenmaszkok alkalmazása… {Math.Min(offset + count, total):N0}/{total:N0}");
            await Dispatcher.Yield(DispatcherPriority.Background);
        }

        profile.ActivatedUtc = DateTime.UtcNow;
        Project.ActiveTokenProfile = profile;
        string folder = Path.Combine(Path.GetDirectoryName(Project.ProjectFilePath)!, "Tokenprofilok");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{LocaPaths.SafeName(profile.ProfileName)}.lutokens.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(profile, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }), new UTF8Encoding(false), cancellationToken);
        _dirty = true;
        UnitsView?.Refresh();
        RaiseHeader();
        RefreshStats();
    }

    private async Task CopyBatchAsync()
    {
        if (Project is null) return;
        try
        {
            PendingBatch batch;
            _suppressUnitRefresh = true;
            try { batch = _clipboard.CreateBatch(Project); }
            finally { _suppressUnitRefresh = false; }
            Project.PendingBatches.Add(batch);
            try
            {
                Clipboard.SetText(batch.ClipboardText);
                _lastClipboardSequence = NativeClipboard.GetClipboardSequenceNumber();
            }
            catch
            {
                _suppressUnitRefresh = true;
                try { _clipboard.CancelBatch(Project, batch.Id); }
                finally { _suppressUnitRefresh = false; }
                throw;
            }
            _dirty = true;
            RefreshStats(); RefreshCommands();
            await _store.SaveAsync(Project); _dirty = false;
            AddLog($"Fordítási csomag mentve és a vágólapra másolva: {batch.UnitIds.Count:N0} sor, {batch.CharacterCount:N0} karakter.");
            NotifyHud(HudTone.Yellow, "Csomag a vágólapon",
                $"{batch.UnitIds.Count:N0} tokenvédett sor. Az AI-választ a program automatikusan felismeri.");
        }
        catch (Exception ex)
        {
            AddLog("Vágólaphiba: " + ex.Message);
            MessageBox.Show(ex.Message, "Vágólapos fordítás", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RecopyBatch()
    {
        PendingBatch? batch = Project?.PendingBatches.OrderByDescending(item => item.CreatedUtc).FirstOrDefault();
        if (batch is null) return;
        try
        {
            Clipboard.SetText(batch.ClipboardText);
            _lastClipboardSequence = NativeClipboard.GetClipboardSequenceNumber();
            AddLog("A legutóbbi mentett, várakozó csomag újra a vágólapra került.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Vágólap", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task ProcessResponseAsync()
    {
        if (Project is null) return;
        try
        {
            string response = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            _lastClipboardSequence = NativeClipboard.GetClipboardSequenceNumber();
            await ProcessClipboardResponseAsync(response, false);
        }
        catch (Exception ex)
        {
            NotifyHud(HudTone.Red, "Hibás vágólapválasz", ex.Message);
            MessageBox.Show(ex.Message, "Vágólapos fordítás", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CancelBatchAsync()
    {
        if (Project is null) return;
        _suppressUnitRefresh = true;
        try { _clipboard.CancelBatch(Project); }
        finally { _suppressUnitRefresh = false; }
        _dirty = true; RefreshStats(); RefreshCommands();
        await _store.SaveAsync(Project); _dirty = false;
        AddLog("A legutóbbi várakozó csomag visszaállítva fordítandó állapotba és a projekt mentve.");
    }

    private void CancelBusy() { BusyMessage = "Megszakítás…"; _busyCancellation?.Cancel(); }

    private async Task OpenProjectSettingsAsync()
    {
        if (Project is null) { MessageBox.Show("Előbb nyiss meg egy projektet."); return; }
        var window = new ProjectSettingsWindow(Project) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() == true)
        {
            _dirty = true; RaiseHeader();
            Raise(nameof(SelectedTranslationMode)); Raise(nameof(PrimaryTranslationLabel)); Raise(nameof(TransportStatusLabel));
            Raise(nameof(ShouldShowStatusHud)); Raise(nameof(ShouldUseGlobalClipboardHotkey)); Raise(nameof(ConfiguredClipboardHotkey));
            GlobalHotkeyConfigurationChanged?.Invoke();
            HudVisibilitySettingsChanged?.Invoke();
            await _store.SaveAsync(Project); _dirty = false;
            string limit = Project.ClipboardLimitMode == ClipboardLimitMode.Rows
                ? $"{Project.ClipboardMaximumItems:N0} sor" : $"{Project.ClipboardMaximumCharacters:N0} karakter";
            AddLog($"Projektbeállítások azonnal mentve: {limit}, mód: {SelectedTranslationMode}, gyorsbillentyű: {Project.ClipboardHotkey}.");
        }
    }

    public bool TryExecuteClipboardHotkey(Key key, ModifierKeys modifiers)
    {
        if (Project is null || IsBusy) return false;
        bool matches = Project.ClipboardHotkey switch
        {
            "Insert" => key == Key.Insert && modifiers == ModifierKeys.None,
            "F8" => key == Key.F8 && modifiers == ModifierKeys.None,
            "F9" => key == Key.F9 && modifiers == ModifierKeys.None,
            "Ctrl+Enter" => key == Key.Enter && modifiers == ModifierKeys.Control,
            _ => false
        };
        if (!matches || !CopyBatchCommand.CanExecute(null)) return false;
        CopyBatchCommand.Execute(null);
        return true;
    }

    public bool TryExecuteGlobalClipboardHotkey()
    {
        if (Project is null || IsBusy || !ShouldUseGlobalClipboardHotkey || !CopyBatchCommand.CanExecute(null)) return false;
        CopyBatchCommand.Execute(null);
        return true;
    }

    public void ReportGlobalHotkeyRegistration(bool success)
    {
        if (Project is null || !ShouldUseGlobalClipboardHotkey) return;
        AddLog(success
            ? $"A(z) {ConfiguredClipboardHotkey} gyorsbillentyű a tálcára kicsinyítve is működik."
            : $"A(z) {ConfiguredClipboardHotkey} globális gyorsbillentyű foglalt; az aktív ablakban továbbra is működik.");
    }

    private void ToggleSkip()
    {
        if (SelectedUnit is null) return;
        if (SelectedUnit.Status == UnitStatus.Skipped) { SelectedUnit.TranslationMasked = string.Empty; SelectedUnit.Status = UnitStatus.Pending; }
        else { SelectedUnit.TranslationMasked = SelectedUnit.SourceDisplay; SelectedUnit.Status = UnitStatus.Skipped; }
        _dirty = true; RefreshStats(); UnitsView?.Refresh();
    }

    public void MarkEdited(TranslationUnit? unit)
    {
        if (unit is null) return;
        if (string.IsNullOrWhiteSpace(unit.TranslationMasked)) unit.Status = UnitStatus.Pending;
        else
        {
            unit.TranslationMasked = TextStructureGuard.Preserve(unit.SourceDisplay, unit.TranslationMasked);
            unit.Status = _protection.ValidateMarkers(unit.SourceDisplay, unit.TranslationMasked, out _) &&
                TextStructureGuard.Validate(unit.SourceDisplay, unit.TranslationMasked, out _) ? UnitStatus.Completed : UnitStatus.NeedsReview;
        }
        if (!string.IsNullOrWhiteSpace(unit.LastAiTranslationMasked) && unit.LastAiTranslationMasked != unit.TranslationMasked)
        {
            unit.TranslationOrigin = "Kézi javítás"; unit.ReviewNotes = "A Gemini-válasz kézzel javítva."; unit.LastAiTranslationMasked = string.Empty;
        }
        _dirty = true; RefreshStats();
    }

    public QualityReport RunQualityAudit()
    {
        if (Project is null) return new QualityReport();
        QualityReport report = new LocalizationQualityService(_protection).Audit(Project);
        HashSet<string> critical = report.Issues.Where(issue => issue.Severity >= QualitySeverity.Error)
            .Select(issue => issue.UnitId).ToHashSet(StringComparer.Ordinal);
        foreach (TranslationUnit unit in Project.Units.Where(unit => critical.Contains(unit.Id) && unit.Status == UnitStatus.Completed))
            unit.Status = UnitStatus.NeedsReview;
        _dirty = true; RefreshStats(); UnitsView?.Refresh();
        AddLog($"Minőségkapu: {report.CheckedUnits:N0} sor, {report.ErrorCount:N0} hiba, {report.WarningCount:N0} figyelmeztetés.");
        return report;
    }

    public async Task UseGameFolderAsync(IReadOnlyList<string> paths, string gameRoot, string engine)
    {
        string fullRoot = Path.GetFullPath(gameRoot);
        if (Project is null)
        {
            string suggestion = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar));
            var dialog = new InputDialog("Új játék-lokalizáció", "Projekt neve", suggestion) { Owner = Application.Current.MainWindow };
            if (dialog.ShowDialog() != true) return;
            await RunBusyAsync("Játékprojekt létrehozása…", async () =>
            {
                var project = new ProjectDocument { Name = dialog.Value, ProjectFilePath = LocaPaths.CreateProjectPath(dialog.Value), GameRootPath = fullRoot, DetectedGameEngine = engine };
                if (paths.Count > 0)
                {
                    ImportedSource imported = await _importer.ImportAsync(paths, sourceLanguage: project.SourceLanguage, targetLanguage: project.TargetLanguage);
                    SourceImporter.Merge(project, imported, false);
                }
                BuiltInTokenProfiles.EnsureApplied(project, _protection);
                await _store.SaveAsync(project); SetProject(project);
            });
            return;
        }
        await RunBusyAsync("Játékszövegek beolvasása…", async () =>
        {
            if (paths.Count > 0)
            {
                ImportedSource imported = await _importer.ImportAsync(paths, sourceLanguage: Project.SourceLanguage, targetLanguage: Project.TargetLanguage);
                SourceImporter.Merge(Project, imported, true);
            }
            Project.GameRootPath = fullRoot; Project.DetectedGameEngine = engine;
            BuiltInTokenProfiles.EnsureApplied(Project, _protection);
            await _store.SaveAsync(Project); SetProject(Project);
        });
    }

    private void SetProject(ProjectDocument project)
    {
        if (Project is not null) foreach (TranslationUnit unit in Project.Units) unit.PropertyChanged -= UnitChanged;
        Project = project;
        foreach (TranslationUnit unit in project.Units) unit.PropertyChanged += UnitChanged;
        UnitsView = CollectionViewSource.GetDefaultView(project.Units); UnitsView.Filter = FilterUnit;
        _lastClipboardSequence = 0;
        _dirty = false; RefreshStats(); RefreshCommands(); RaiseHeader();
        Raise(nameof(SelectedTranslationMode)); Raise(nameof(PrimaryTranslationLabel)); Raise(nameof(TransportStatusLabel));
        Raise(nameof(ShouldShowStatusHud)); Raise(nameof(ShouldUseGlobalClipboardHotkey)); Raise(nameof(ConfiguredClipboardHotkey));
        GlobalHotkeyConfigurationChanged?.Invoke();
    }

    private bool FilterUnit(object item)
    {
        if (item is not TranslationUnit unit) return false;
        if (StatusFilter == "Aktív szövegek" && !unit.Active) return false;
        if (StatusFilter != "Aktív szövegek" && unit.StatusLabel != StatusFilter) return false;
        return string.IsNullOrWhiteSpace(SearchText) || unit.SourceDisplay.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
            unit.TranslationMasked.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) || unit.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
            unit.ContextDisplay.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);
    }
    private void UnitChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressUnitRefresh) return;
        _dirty = true; RefreshStats();
    }
    private void RefreshStats()
    {
        TranslationUnit[] active = Project?.Units.Where(unit => unit.Active).ToArray() ?? [];
        TotalCount = active.Length; CompletedCount = active.Count(unit => unit.Status is UnitStatus.Completed or UnitStatus.Skipped);
        PendingCount = active.Count(unit => unit.Status == UnitStatus.Pending); WaitingCount = active.Count(unit => unit.Status == UnitStatus.Waiting);
        ReviewCount = active.Count(unit => unit.Status == UnitStatus.NeedsReview);
        Raise(nameof(ProgressLabel)); Raise(nameof(ProgressValue)); Raise(nameof(PendingPackageCount)); Raise(nameof(TransportStatusLabel));
    }

    private async Task ObserveClipboardAsync()
    {
        if (_clipboardProcessing || IsBusy || Project is null || !Project.AutoProcessClipboardResponses || Project.PendingBatches.Count == 0) return;
        uint sequence = NativeClipboard.GetClipboardSequenceNumber();
        if (sequence == 0 || sequence == _lastClipboardSequence) return;
        _lastClipboardSequence = sequence;
        string response;
        try { response = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
        catch (COMException) { return; }
        if (string.IsNullOrWhiteSpace(response)) return;
        if (Project.PendingBatches.Any(batch => string.Equals(batch.ClipboardText.Trim(), response.Trim(), StringComparison.Ordinal))) return;
        if (!_clipboard.ContainsResponseForAnyBatch(Project, response)) return;
        try { await ProcessClipboardResponseAsync(response, true); }
        catch (Exception ex)
        {
            AddLog("Automatikus vágólapfeldolgozási hiba: " + ex.Message);
            NotifyHud(HudTone.Red, "Hibás vágólapválasz", ex.Message);
        }
    }

    private async Task ProcessClipboardResponseAsync(string response, bool automatic)
    {
        if (Project is null || _clipboardProcessing) return;
        _clipboardProcessing = true;
        try
        {
            IReadOnlyList<BatchApplyResult> results;
            _suppressUnitRefresh = true;
            try { results = _clipboard.ApplyResponses(Project, response); }
            finally { _suppressUnitRefresh = false; }
            int applied = results.Sum(result => result.Applied);
            int rejected = results.Sum(result => result.Rejected);
            int missing = results.Sum(result => result.Missing);
            string[] errors = results.SelectMany(result => result.Errors).ToArray();
            _dirty = true; RefreshStats(); RefreshCommands(); UnitsView?.Refresh();
            await _store.SaveAsync(Project); _dirty = false;
            AddLog($"Vágólapválasz feldolgozva és mentve: {results.Count:N0} csomag, {applied:N0} kész, {rejected:N0} elutasítva, {missing:N0} hiányzik.");
            if (rejected == 0 && missing == 0 && errors.Length == 0 && applied > 0)
                NotifyHud(HudTone.Green, "Csomag kész", $"{applied:N0} sor hibátlanul visszaépült és elmentődött.");
            else NotifyHud(HudTone.Red, "Hibás csomag", $"Kész: {applied:N0} · hibás: {rejected:N0} · hiányzik: {missing:N0}");
            if (!automatic && errors.Length > 0)
                MessageBox.Show(string.Join("\n", errors.Take(20)), "Feldolgozási figyelmeztetés", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _clipboardProcessing = false; }
    }

    private void NotifyHud(HudTone tone, string title, string message)
    {
        Raise(nameof(ShouldShowStatusHud));
        if (!ShouldShowStatusHud) return;
        HudStatusChanged?.Invoke(new HudStatus(tone, title, message, PendingCount, WaitingCount, PendingPackageCount,
            Project?.StatusHudDisplaySeconds ?? 4));
    }

    private static string[]? SelectSourceFiles(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Multiselect = true,
            Filter = "Lokalizációs források|*.txt;*.jsonl;*.lutext;*.po;*.csv;*.tsv;*.json;*.xml;*.ini;*.lang;*.yml;*.yaml;*.resx|Minden fájl (*.*)|*.*" };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }
    private static bool ConfirmDetectedTextLayouts(IEnumerable<SourceDocumentInfo> documents)
    {
        string[] descriptions = documents.Where(document => document.Format == SourceDocumentFormat.PlainText && document.TextLayoutKind != PlainTextLayoutKind.WholeLine)
            .Take(8).Select(document => $"• {document.OriginalFileName}: {document.TextLayoutDescription}").ToArray();
        return descriptions.Length == 0 || MessageBox.Show("Felismert TXT-elrendezés:\n" + string.Join("\n", descriptions) + "\n\nFolytatod?",
            "TXT célhelyének ellenőrzése", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        IsBusy = true; BusyMessage = message;
        try { await action(); }
        catch (OperationCanceledException) { AddLog("A művelet megszakadt."); }
        catch (Exception ex) { AddLog("Hiba: " + ex.Message); MessageBox.Show(ex.Message, "LocaUltrator 0.8.7", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { BusyMessage = string.Empty; IsBusy = false; RefreshStats(); }
    }
    private void AddLog(string text) { Logs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}"); while (Logs.Count > 200) Logs.RemoveAt(Logs.Count - 1); }
    private static void ShowHelp() => MessageBox.Show(
        "1. Hozz létre projektet, majd készíts és aktiválj projekt-tokenprofilt.\n2. A Gemini ablakban vedd fel a kulcsokat; mindegyik engedélyezett kulcs részt vesz a soros körforgásban.\n3. Add meg kulcsonként a Free/Tier szintet, az AI Studio aktív 3.7 Flash és 3.1 Pro RPM/TPM/RPD limitjeit és a projektcsoportot.\n4. Free Tieren csak 3.7 Flash működik; fizetős Tieren a program dinamikusan vált 3.7 Flash és 3.1 Pro között.\n5. Teszteld a kulcsokat: a kulcsellenőrzés külön történik a Google modellterhelésétől.\n6. Válassz Automatikus, Gemini API vagy Vágólap módot. Automatikus módban a modell- és kulcsváltás után a vágólap következik.\n7. Vágólap módban másold a csomagot az AI-nak; a válasz visszamásolásakor a program automatikusan feldolgozza és elmenti.\n8. A várakozó csomagok a projektben maradnak, ezért újraindítás után is folytathatók.\n9. Futtasd a minőségkaput, majd exportálj.\n\nA tokenmaszkok, sortörések, fájlformátum és kódolás minden munkamódban ellenőrzöttek.",
        "LocaUltrator 0.8.7 – Súgó", MessageBoxButton.OK, MessageBoxImage.Information);

    private void RaiseHeader() { Raise(nameof(ProjectName)); Raise(nameof(ProjectPath)); Raise(nameof(TokenProfileLabel)); Raise(nameof(SourceVersionLabel)); Raise(nameof(SelectedTranslationMode)); Raise(nameof(PrimaryTranslationLabel)); Raise(nameof(TransportStatusLabel)); }
    private void RefreshCommands()
    {
        NewProjectCommand.Raise(); OpenProjectCommand.Raise(); SaveCommand.Raise(); ImportUpdateCommand.Raise(); ExportCommand.Raise(); TranslateCommand.Raise();
        CopyBatchCommand.Raise(); RecopyBatchCommand.Raise(); ProcessResponseCommand.Raise(); CancelBatchCommand.Raise();
        TokenPromptCommand.Raise(); ProcessTokenProfileCommand.Raise(); ProjectSettingsCommand.Raise(); ToggleSkipCommand.Raise(); CancelBusyCommand.Raise();
    }
    public async Task ShutdownAsync() { try { if (_dirty && Project is not null) await _store.SaveAsync(Project); } catch { } }
    public void Dispose() { _autosave.Stop(); _clipboardMonitor.Stop(); _busyCancellation?.Cancel(); _busyCancellation?.Dispose(); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static class NativeClipboard
    {
        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();
    }
}
