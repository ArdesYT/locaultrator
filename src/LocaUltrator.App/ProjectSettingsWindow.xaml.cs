using System.Windows;
using LocaUltrator.Core;

namespace LocaUltrator.App;

public partial class ProjectSettingsWindow : Window
{
    private static readonly string[] Hotkeys = ["Insert", "F8", "F9", "Ctrl+Enter", "Kikapcsolva"];
    private const string RowsMode = "Sorok száma alapján";
    private const string CharactersMode = "Karakterszám alapján";
    private const string StandardAccents = "Normál magyar: ő ű Ő Ű";
    private const string CircumflexAccents = "Kalapos játékforma: ô û Ô Û";
    private const string AutomaticTransport = "Automatikus: API → vágólap";
    private const string ApiTransport = "Csak Gemini API";
    private const string ClipboardTransport = "Csak vágólap";
    private readonly ProjectDocument _project;
    public ProjectSettingsWindow(ProjectDocument project)
    {
        InitializeComponent();
        _project = project;
        TargetLanguage.Text = project.TargetLanguage;
        MaximumItems.Text = project.ClipboardMaximumItems.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MaximumCharacters.Text = project.ClipboardMaximumCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MaximumPendingBatches.Text = project.MaximumPendingBatches.ToString(System.Globalization.CultureInfo.InvariantCulture);
        StatusHudSeconds.Text = project.StatusHudDisplaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        LimitMode.ItemsSource = new[] { RowsMode, CharactersMode };
        LimitMode.SelectedItem = project.ClipboardLimitMode == ClipboardLimitMode.Characters ? CharactersMode : RowsMode;
        ClipboardHotkey.ItemsSource = Hotkeys;
        ClipboardHotkey.SelectedItem = Hotkeys.Contains(project.ClipboardHotkey, StringComparer.Ordinal) ? project.ClipboardHotkey : "Insert";
        ExportAccentMode.ItemsSource = new[] { StandardAccents, CircumflexAccents };
        ExportAccentMode.SelectedItem = project.ExportAccentMode == HungarianAccentMode.Circumflex
            ? CircumflexAccents : StandardAccents;
        TranslationMode.ItemsSource = new[] { AutomaticTransport, ApiTransport, ClipboardTransport };
        TranslationMode.SelectedItem = project.TranslationTransport switch
        {
            TranslationTransport.GeminiApi => ApiTransport,
            TranslationTransport.Clipboard => ClipboardTransport,
            _ => AutomaticTransport
        };
        AutoProcessResponses.IsChecked = project.AutoProcessClipboardResponses;
        ShowStatusHud.IsChecked = project.ShowStatusHud;
        GlobalClipboardHotkey.IsChecked = project.EnableGlobalClipboardHotkey;
        Instruction.Text = project.TranslationInstruction;
        UpdateLimitInputs();
    }
    private void LimitModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateLimitInputs();
    private void UpdateLimitInputs()
    {
        if (MaximumItems is null || MaximumCharacters is null || LimitMode is null) return;
        bool rows = !string.Equals(LimitMode.SelectedItem as string, CharactersMode, StringComparison.Ordinal);
        MaximumItems.IsEnabled = rows;
        MaximumCharacters.IsEnabled = !rows;
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TargetLanguage.Text)) { MessageBox.Show("Add meg a célnyelvet."); return; }
        if (!int.TryParse(MaximumItems.Text, out int maximumItems) || maximumItems is < 1 or > 5_000)
        {
            MessageBox.Show("A maximális sorszám 1 és 5000 közötti egész szám legyen.", "Hibás beállítás", MessageBoxButton.OK, MessageBoxImage.Warning);
            MaximumItems.Focus(); MaximumItems.SelectAll(); return;
        }
        if (!int.TryParse(MaximumCharacters.Text, out int maximumCharacters) || maximumCharacters is < 1_000 or > 1_000_000)
        {
            MessageBox.Show("A maximális karakterszám 1000 és 1 000 000 közötti egész szám legyen.", "Hibás beállítás", MessageBoxButton.OK, MessageBoxImage.Warning);
            MaximumCharacters.Focus(); MaximumCharacters.SelectAll(); return;
        }
        if (!int.TryParse(MaximumPendingBatches.Text, out int maximumPendingBatches) || maximumPendingBatches is < 1 or > 20)
        {
            MessageBox.Show("Az egyszerre várakozó csomagok száma 1 és 20 közötti egész szám legyen.", "Hibás beállítás", MessageBoxButton.OK, MessageBoxImage.Warning);
            MaximumPendingBatches.Focus(); MaximumPendingBatches.SelectAll(); return;
        }
        if (!int.TryParse(StatusHudSeconds.Text, out int statusHudSeconds) || statusHudSeconds is < 1 or > 60)
        {
            MessageBox.Show("Az értesítés láthatósága 1 és 60 másodperc közötti egész szám legyen.", "Hibás beállítás", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusHudSeconds.Focus(); StatusHudSeconds.SelectAll(); return;
        }
        _project.TargetLanguage = TargetLanguage.Text.Trim();
        _project.ClipboardMaximumItems = maximumItems;
        _project.ClipboardMaximumCharacters = maximumCharacters;
        _project.ClipboardLimitMode = string.Equals(LimitMode.SelectedItem as string, CharactersMode, StringComparison.Ordinal)
            ? ClipboardLimitMode.Characters : ClipboardLimitMode.Rows;
        _project.MaximumPendingBatches = maximumPendingBatches;
        _project.AutoProcessClipboardResponses = AutoProcessResponses.IsChecked == true;
        _project.ShowStatusHud = ShowStatusHud.IsChecked == true;
        _project.StatusHudDisplaySeconds = statusHudSeconds;
        _project.EnableGlobalClipboardHotkey = GlobalClipboardHotkey.IsChecked == true;
        _project.ClipboardHotkey = ClipboardHotkey.SelectedItem as string ?? "Insert";
        _project.ExportAccentMode = string.Equals(ExportAccentMode.SelectedItem as string, CircumflexAccents, StringComparison.Ordinal)
            ? HungarianAccentMode.Circumflex : HungarianAccentMode.Standard;
        _project.TranslationTransport = (TranslationMode.SelectedItem as string) switch
        {
            ApiTransport => TranslationTransport.GeminiApi,
            ClipboardTransport => TranslationTransport.Clipboard,
            _ => TranslationTransport.Automatic
        };
        _project.TranslationInstruction = Instruction.Text.Trim();
        DialogResult = true;
    }
}
