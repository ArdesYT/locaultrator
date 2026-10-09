using System.Windows;

namespace LocaUltrator.App;

public enum NewLocalizationSource
{
    Files,
    GameFolder
}

public partial class NewLocalizationWindow : Window
{
    public NewLocalizationSource Source { get; private set; }

    public NewLocalizationWindow() => InitializeComponent();

    private void OnGameFolder(object sender, RoutedEventArgs e)
    {
        Source = NewLocalizationSource.GameFolder;
        DialogResult = true;
    }

    private void OnFiles(object sender, RoutedEventArgs e)
    {
        Source = NewLocalizationSource.Files;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
