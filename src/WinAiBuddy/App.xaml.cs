using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using WinAiBuddy.Services;
using Application = System.Windows.Application;

namespace WinAiBuddy;

public partial class App : Application
{
    private NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private GameAssistantOrchestrator? _orchestrator;
    private GeminiLiveSessionService? _liveSessionService;
    private AudioRecordingService? _audioRecordingService;
    private SpeechPlaybackService? _speechPlaybackService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinAiBuddy",
            "appsettings.json");

        var settingsService = new SettingsService(settingsPath);
        settingsService.EnsureLoaded();

        var overlayWindow = new OverlayWindow();
        var overlayService = new OverlayService(overlayWindow);
        var screenCaptureService = new ScreenCaptureService();
        _audioRecordingService = new AudioRecordingService();
        _liveSessionService = new GeminiLiveSessionService();
        _speechPlaybackService = new SpeechPlaybackService();

        _orchestrator = new GameAssistantOrchestrator(
            () => settingsService.Current,
            screenCaptureService,
            _audioRecordingService,
            _liveSessionService,
            overlayService,
            _speechPlaybackService);

        _mainWindow = new MainWindow(settingsService, _orchestrator, overlayService);
        _mainWindow.Icon = AppIconProvider.LoadWindowIcon();
        _mainWindow.Show();

        if (settingsService.Current.StartMinimizedToTray)
        {
            _mainWindow.Hide();
        }

        ConfigureTrayIcon();
        _ = _mainWindow.StartLiveIfConfiguredAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _orchestrator?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _liveSessionService?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _audioRecordingService?.Dispose();
        _speechPlaybackService?.Dispose();

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        base.OnExit(e);
    }

    private void ConfigureTrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = AppIconProvider.LoadTrayIcon(),
            Text = "Win AI Buddy",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => _mainWindow?.ShowFromTray());
        menu.Items.Add("Start Live", null, async (_, _) => await (_orchestrator?.StartLiveSessionAsync() ?? Task.CompletedTask));
        menu.Items.Add("Stop Live", null, async (_, _) => await (_orchestrator?.StopLiveSessionAsync() ?? Task.CompletedTask));
        menu.Items.Add("Exit", null, (_, _) => ShutdownFromTray());

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => _mainWindow?.ShowFromTray();
    }

    private void ShutdownFromTray()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.PrepareForExit();
            _mainWindow.Close();
        }

        Shutdown();
    }
}
