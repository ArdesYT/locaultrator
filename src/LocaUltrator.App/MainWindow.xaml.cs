using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using LocaUltrator.Core;

namespace LocaUltrator.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private StatusHudWindow? _statusHud;
    private HwndSource? _windowSource;
    private bool _globalHotkeyRegistered;
    private bool _closingAfterSave;
    private bool _closeScheduled;
    private const int HotkeyId = 0x4C55;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.HudStatusChanged += OnHudStatusChanged;
        _viewModel.GlobalHotkeyConfigurationChanged += RegisterConfiguredHotkey;
        _viewModel.HudVisibilitySettingsChanged += OnHudVisibilitySettingsChanged;
    }

    private void OnHudStatusChanged(HudStatus status) => Dispatcher.InvokeAsync(() =>
    {
        if (!_viewModel.ShouldShowStatusHud) { _statusHud?.Hide(); return; }
        _statusHud ??= new StatusHudWindow();
        _statusHud.UpdateStatus(status);
    });

    private void OnHudVisibilitySettingsChanged()
    {
        if (!_viewModel.ShouldShowStatusHud) Dispatcher.InvokeAsync(() => _statusHud?.Hide());
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource.AddHook(WindowMessageHook);
        RegisterConfiguredHotkey();
    }

    private void RegisterConfiguredHotkey() => Dispatcher.InvokeAsync(() =>
    {
        UnregisterConfiguredHotkey();
        if (_windowSource is null || !_viewModel.ShouldUseGlobalClipboardHotkey) return;
        (uint modifiers, uint virtualKey)? hotkey = _viewModel.ConfiguredClipboardHotkey switch
        {
            "Insert" => (0u, 0x2Du),
            "F8" => (0u, 0x77u),
            "F9" => (0u, 0x78u),
            "Ctrl+Enter" => (ModControl, 0x0Du),
            _ => null
        };
        if (hotkey is null) return;
        _globalHotkeyRegistered = RegisterHotKey(_windowSource.Handle, HotkeyId,
            hotkey.Value.modifiers | ModNoRepeat, hotkey.Value.virtualKey);
        _viewModel.ReportGlobalHotkeyRegistration(_globalHotkeyRegistered);
    });

    private void UnregisterConfiguredHotkey()
    {
        if (!_globalHotkeyRegistered || _windowSource is null) return;
        UnregisterHotKey(_windowSource.Handle, HotkeyId);
        _globalHotkeyRegistered = false;
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(() => _viewModel.TryExecuteGlobalClipboardHotkey());
        }
        return IntPtr.Zero;
    }

    private void OnRowEditEnding(object sender, DataGridRowEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(() => _viewModel.MarkEdited(e.Row.Item as TranslationUnit));

    private void OnRunQuality(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Project is null) { MessageBox.Show("Előbb nyiss meg vagy hozz létre egy projektet."); return; }
        new QualityWindow(_viewModel.RunQualityAudit()) { Owner = this }.ShowDialog();
    }

    private async void OnOpenGameIntegration(object sender, RoutedEventArgs e)
    {
        var window = new GameIntegrationWindow { Owner = this };
        if (window.ShowDialog() == true)
            await _viewModel.UseGameFolderAsync(window.SelectedImportFiles, window.GameRoot, window.DetectedEngine);
    }

    private void OnOpenAiSettings(object sender, RoutedEventArgs e)
    {
        var window = new AiSettingsWindow { Owner = this };
        window.ShowDialog();
        _viewModel.RefreshGeminiStatus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_globalHotkeyRegistered && _viewModel.TryExecuteClipboardHotkey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closingAfterSave)
        {
            UnregisterConfiguredHotkey();
            if (_windowSource is not null) _windowSource.RemoveHook(WindowMessageHook);
            _statusHud?.Close();
            _viewModel.Dispose();
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (_closeScheduled) return;
        _closeScheduled = true;
        _ = SaveAndCloseAsync();
    }

    private async Task SaveAndCloseAsync()
    {
        IsEnabled = false;
        try { await _viewModel.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _closingAfterSave = true;
        Close();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        UnitStatus.Completed => new SolidColorBrush(Color.FromRgb(52, 211, 153)),
        UnitStatus.Pending => new SolidColorBrush(Color.FromRgb(251, 191, 36)),
        UnitStatus.Waiting => new SolidColorBrush(Color.FromRgb(34, 211, 238)),
        UnitStatus.NeedsReview => new SolidColorBrush(Color.FromRgb(251, 113, 133)),
        UnitStatus.Skipped => new SolidColorBrush(Color.FromRgb(148, 163, 184)),
        _ => new SolidColorBrush(Color.FromRgb(100, 116, 139))
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
