using System.Windows;

namespace LocaUltrator.App;

public partial class InputDialog : Window
{
    public string Value => Answer.Text.Trim();
    public InputDialog(string title, string question, string initial)
    {
        InitializeComponent(); Title = title; Question.Text = question; Answer.Text = initial;
        Loaded += (_, _) => { Answer.Focus(); Answer.SelectAll(); };
    }
    private void Accept(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Value)) { MessageBox.Show("Az érték nem lehet üres.", Title); return; }
        DialogResult = true;
    }
}
