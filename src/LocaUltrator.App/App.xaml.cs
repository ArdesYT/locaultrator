using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LocaUltrator.Core;

namespace LocaUltrator.App;

public partial class App : Application
{
    public static SettingsStore SettingsStore { get; } = new();
    public static GeminiKeyFileStore KeyFileStore { get; } = new();
    public static GeminiTranslationService Gemini { get; } = new();
    public static GeminiSettings Settings { get; set; } = new();
    public static List<GeminiApiKeyEntry> ApiKeys { get; set; } = [];
    public static string ApiKey
    {
        get => OrderedEnabledApiKeys().FirstOrDefault()?.Secret ?? string.Empty;
        set
        {
            ApiKeys = string.IsNullOrWhiteSpace(value)
                ? []
                : [new GeminiApiKeyEntry { Name = "Elsődleges kulcs", Secret = value.Trim(), Enabled = true, IsActive = true }];
        }
    }

    public static IReadOnlyList<GeminiApiKeyEntry> OrderedEnabledApiKeys() => ApiKeys
        .Where(key => key.Enabled && !string.IsNullOrWhiteSpace(key.Secret))
        .Select(key => key.Clone())
        .ToArray();

    public static void ApplyKeyRuntimeStatus(TranslationRunResult result)
    {
        foreach (GeminiApiKeyEntry key in ApiKeys)
        {
            if (result.KeyStates.TryGetValue(key.Id, out GeminiKeyState state)) key.RuntimeState = state;
            if (result.KeyMessages.TryGetValue(key.Id, out string? message) && !string.IsNullOrWhiteSpace(message))
                key.RuntimeMessage = message;
            if (result.KeyUsage.GetValueOrDefault(key.Id) > 0) key.LastCheckedUtc = DateTime.UtcNow;
        }
    }
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LocaPaths.EnsureCreated();
        bool uiSelfTest = e.Args.Contains("--self-test-ui", StringComparer.OrdinalIgnoreCase);
        bool uiSelfTestFailed = false;
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Settings = await SettingsStore.LoadAsync();
            _ = BuiltInTokenProfiles.CreateSafeDefault();
            Shutdown(0);
            return;
        }

        if (!uiSelfTest)
        {
            _singleInstanceMutex = new Mutex(true, @"Local\LocaUltrator.Desktop.SingleInstance.v2", out bool createdNew);
            _ownsSingleInstanceMutex = createdNew;
            if (!createdNew)
            {
                MessageBox.Show("A LocaUltrator már fut. Válts át a meglévő ablakára.",
                    "LocaUltrator 0.8.7", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }
        }
        Settings = await SettingsStore.LoadAsync();
        try
        {
            if (KeyFileStore.Exists)
            {
                ApiKeys = (await KeyFileStore.LoadAsync()).Select(key => key.Clone()).ToList();
            }
            else
            {
                ApiKeys = GeminiCredentialStore.ReadAll().Select(key => key.Clone()).ToList();
                await KeyFileStore.SaveAsync(ApiKeys);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ApiKeys = GeminiCredentialStore.ReadAll().Select(key => key.Clone()).ToList();
            MessageBox.Show(
                ex.Message + "\n\nA fájlt a program nem írta felül. A Windows biztonsági mentésből betöltötte az elérhető kulcsokat.",
                "Gemini-kulcsfájl", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        DispatcherUnhandledException += (_, args) =>
        {
            if (uiSelfTest)
            {
                uiSelfTestFailed = true;
                args.Handled = true;
                return;
            }
            try
            {
                string file = Path.Combine(LocaPaths.Logs, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
                File.WriteAllText(file, JsonSerializer.Serialize(new
                {
                    utc = DateTime.UtcNow,
                    error = args.Exception.Message,
                    type = args.Exception.GetType().FullName,
                    details = args.Exception.ToString()
                }));
            }
            catch { }
            MessageBox.Show(args.Exception.Message, "LocaUltrator 0.8.7", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        MainWindow = new MainWindow();
        MainWindow.Show();
        if (uiSelfTest)
        {
            var settingsWindow = new AiSettingsWindow { Owner = MainWindow };
            settingsWindow.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            settingsWindow.Close();
            Shutdown(uiSelfTestFailed ? 1 : 0);
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !OperatingSystem.IsWindows()) return;
        try
        {
            nint handle = new WindowInteropHelper(window).Handle;
            int enabled = 1;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
        catch
        {
            // Régebbi Windows-verzión a natív címsor marad rendszeralapértelmezett.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int valueSize);

    protected override void OnExit(ExitEventArgs e)
    {
        Gemini.Dispose();
        if (_ownsSingleInstanceMutex) { try { _singleInstanceMutex?.ReleaseMutex(); } catch { } }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
