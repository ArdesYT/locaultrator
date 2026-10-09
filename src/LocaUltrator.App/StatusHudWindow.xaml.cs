using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace LocaUltrator.App;

public enum HudTone { Yellow, Green, Red }

public sealed record HudStatus(HudTone Tone, string Title, string Message, int Remaining, int Waiting, int Packages, int DisplaySeconds);

public partial class StatusHudWindow : Window
{
    private static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(180, 116, 12));
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(5, 137, 91));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(190, 45, 62));
    private readonly DispatcherTimer _hideTimer;

    public StatusHudWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
    }

    public void UpdateStatus(HudStatus status)
    {
        Surface.Background = status.Tone switch { HudTone.Green => Green, HudTone.Red => Red, _ => Yellow };
        TitleText.Text = status.Title;
        MessageText.Text = status.Message;
        RemainingText.Text = status.Remaining.ToString("N0");
        WaitingText.Text = status.Waiting.ToString("N0");
        PackagesText.Text = status.Packages.ToString("N0");

        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
        if (!IsVisible) Show();
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(status.DisplaySeconds, 1, 60));
        _hideTimer.Start();
    }

    protected override void OnClosed(EventArgs e) { _hideTimer.Stop(); base.OnClosed(e); }
}
