using System.Windows;
using System.Windows.Controls;
using System.IO;
using LocaUltrator.Core;
using Microsoft.Win32;

namespace LocaUltrator.App;

public partial class GameIntegrationWindow : Window
{
    private readonly GameIntegrationService _service = new();
    private GameEngineDetection? _detection;
    public IReadOnlyList<string> SelectedImportFiles { get; private set; } = [];
    public string GameRoot => GameRootBox.Text.Trim();
    public string DetectedEngine => _detection?.Engine ?? "Ismeretlen";
    public bool RequiresExtraction => (_detection?.ArchiveFiles.Count ?? 0) > 0;
    public string DetectionSummary => _detection is null
        ? "Nincs felismerési eredmény."
        : $"{_detection.LooseLocalizationFiles.Count:N0} laza lokalizációs jelölt, {_detection.ArchiveFiles.Count:N0} archívum. {_detection.Recommendation}";

    public GameIntegrationWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => AdapterBox.ItemsSource = _service.LoadManifests();
    }

    private void OnBrowseGame(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Játék telepítési mappája" };
        if (dialog.ShowDialog(this) == true)
        {
            GameRootBox.Text = dialog.FolderName;
            DetectGame();
        }
    }

    private void OnDetect(object sender, RoutedEventArgs e) => DetectGame();

    private void DetectGame()
    {
        try
        {
            _detection = _service.Detect(GameRoot);
            DetectionTitle.Text = $"Motor: {_detection.Engine} · {_detection.Confidence:P0} bizonyosság";
            DetectionDetail.Text = string.Join(" ", _detection.Evidence) + " " + _detection.Recommendation;
            string[] directExtensions = [".txt", ".jsonl", ".lutext", ".po", ".csv", ".tsv", ".json", ".xml", ".ini", ".lang", ".yml", ".yaml", ".resx"];
            string[] supported = _detection.LooseLocalizationFiles.Where(path => directExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
            LooseFilesList.ItemsSource = supported;
            FileCountText.Text = $"{supported.Length:N0} közvetlenül olvasható fájl lesz kijelölve · {_detection.ArchiveFiles.Count:N0} archívum. Kijelölés nélkül is létrehozható a játékprojekt.";
            if (supported.Length > 0) LooseFilesList.SelectAll();
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Játékfelismerés", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(GameRoot)) { System.Windows.MessageBox.Show("Előbb válaszd ki a játék mappáját."); return; }
        if (_detection is null) DetectGame();
        if (_detection is null) return;
        SelectedImportFiles = LooseFilesList.SelectedItems.Cast<string>().ToArray();
        if (SelectedImportFiles.Count == 0 && _detection.ArchiveFiles.Count == 0)
        {
            MessageBoxResult confirmation = System.Windows.MessageBox.Show(
                "Nem találtam kijelölt, közvetlenül olvasható lokalizációs fájlt és ismert archívumot. A mappát ettől még hozzárendelhetem a projekthez későbbi adapteres feldolgozáshoz. Folytatod?",
                "Játékmappa használata", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes) return;
        }
        DialogResult = true;
    }

    private void OnAdapterChanged(object sender, SelectionChangedEventArgs e) =>
        OperationBox.ItemsSource = (AdapterBox.SelectedItem as GameToolManifest)?.Operations;

    private void OnBrowseInput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Fordított bemeneti fájl", Filter = "Minden fájl|*.*" };
        if (dialog.ShowDialog(this) == true) TranslatedInputBox.Text = dialog.FileName;
    }

    private async void OnRunAdapter(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not GameToolManifest manifest || OperationBox.SelectedItem is not GameToolOperation operation)
        { System.Windows.MessageBox.Show("Válassz adaptert és műveletet."); return; }
        if (!Directory.Exists(GameRoot)) { System.Windows.MessageBox.Show("Előbb válaszd ki a játék mappáját."); return; }
        MessageBoxResult confirm = System.Windows.MessageBox.Show(
            $"Adapter: {manifest.Name} {manifest.Version}\nMűvelet: {operation.DisplayName}\nLicenc: {manifest.License}\n\n" +
            "Az adapter külső program. A LocaUltrator külön munkaterületet és futási naplót készít, de csak megbízható adaptert engedélyezz. Folytatod?",
            "Külső adapter futtatása", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        AdapterLog.Text = "Futás…";
        try
        {
            GameToolRunResult result = await _service.RunAdapterAsync(manifest, operation, GameRoot, TranslatedInputBox.Text.Trim());
            AdapterLog.Text = $"Siker: {result.Success}\nKilépési kód: {result.ExitCode}\nMunkaterület: {result.WorkspacePath}\nKimenet: {result.ExpectedOutputPath}\nJelentés: {result.ReportPath}\n\n{result.StandardOutput}\n{result.StandardError}";
        }
        catch (Exception ex) { AdapterLog.Text = "Hiba: " + ex.Message; }
    }
}
