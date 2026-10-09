using System.Windows;
using LocaUltrator.Core;

namespace LocaUltrator.App;

public partial class PromptWindow : Window
{
    private readonly Func<string, string> _fill;
    private readonly Action<string> _onCopied;

    public string TemplateText => Editor.Text;

    public PromptWindow(string template, Func<string, string> fill, Action<string> onCopied)
    {
        InitializeComponent();
        Editor.Text = template;
        _fill = fill;
        _onCopied = onCopied;
    }

    private void Reset(object sender, RoutedEventArgs e) => Editor.Text = DefaultPrompts.TokenProfilePrompt;

    private void CopyFilled(object sender, RoutedEventArgs e)
    {
        if (!HasRequiredPlaceholders()) return;
        string filledPrompt = _fill(Editor.Text);
        Clipboard.SetText(filledPrompt);
        _onCopied(filledPrompt);
        MessageBox.Show(
            "A kitöltött tokenvédelmi prompt a vágólapra került, és TOKENMASZKOLASI_PROMPT.txt néven a projekt forrás-pillanatképei mellé is elmentettem.\n\n" +
            "Add ezt és a megfelelő TXT-forrást az AI-nak. Ezután kizárólag az AI JSON-válaszát másold a vágólapra, majd kattints a ‘2. AI JSON-profil betöltése’ gombra.",
            "Tokenprompt elkészült", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (!HasRequiredPlaceholders()) return;
        DialogResult = true;
    }

    private bool HasRequiredPlaceholders()
    {
        string[] required = ["{{PROJECT_NAME}}", "{{TOKEN_CANDIDATES_JSON}}", "{{OUTPUT_SCHEMA}}"];
        string[] missing = required.Where(value => !Editor.Text.Contains(value, StringComparison.Ordinal)).ToArray();
        if (missing.Length == 0) return true;
        MessageBox.Show("A promptból hiányzik: " + string.Join(", ", missing), "Hiányzó helyőrző",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
