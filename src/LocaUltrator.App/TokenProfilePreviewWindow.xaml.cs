using System.Text;
using System.Windows;
using LocaUltrator.Core;

namespace LocaUltrator.App;

public partial class TokenProfilePreviewWindow : Window
{
    public TokenProfilePreviewWindow(TokenProfile profile, TokenProfileValidation validation)
    {
        InitializeComponent();
        Summary.Text = $"Profil: {profile.ProfileName}  ·  Szabályok: {profile.Rules.Count}  ·  " +
            $"Literális tokenek: {profile.Literals.Count}  ·  Ellenőrzött projekttalálatok: {validation.MatchCount:N0}";
        var text = new StringBuilder();
        foreach (TokenRule rule in profile.Rules.OrderByDescending(value => value.Priority))
        {
            text.AppendLine($"[{rule.Category}] {rule.Name} ({rule.Id})");
            text.AppendLine("Regex: " + rule.Pattern);
            text.AppendLine("Valódi projektpéldák: " + (rule.Examples.Count == 0 ? "–" : string.Join(", ", rule.Examples)));
            text.AppendLine();
        }
        if (profile.Literals.Count > 0)
        {
            text.AppendLine("LITERÁLIS TOKENEK");
            foreach (LiteralToken literal in profile.Literals) text.AppendLine($"[{literal.Category}] {literal.Value}");
            text.AppendLine();
        }
        if (profile.Warnings.Count > 0)
        {
            text.AppendLine("AI FIGYELMEZTETÉSEK");
            foreach (string warning in profile.Warnings) text.AppendLine("- " + warning);
        }
        Details.Text = text.ToString();
    }

    private void Activate(object sender, RoutedEventArgs e) => DialogResult = true;
}
