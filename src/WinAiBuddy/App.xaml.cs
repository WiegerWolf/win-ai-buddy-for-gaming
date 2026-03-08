using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using WinAiBuddy.Services;
using Application = System.Windows.Application;

namespace WinAiBuddy;

public partial class App : Application
{
    private const string ThemeDictionaryPrefix = "Themes/";
    private NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private GameAssistantOrchestrator? _orchestrator;
    private GeminiLiveSessionService? _liveSessionService;
    private AudioRecordingService? _audioRecordingService;
    private SpeechPlaybackService? _speechPlaybackService;
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinAiBuddy",
            "appsettings.json");
        var conversationSessionsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinAiBuddy",
            "Conversations");

        var settingsService = new SettingsService(settingsPath);
        settingsService.EnsureLoaded();
        ApplyTheme(settingsService.Current.AppTheme);
        var conversationSessionStore = new ConversationSessionStore(conversationSessionsPath);
        conversationSessionStore.RecoverInterruptedSessions();

        var overlayWindow = new OverlayWindow();
        var overlayService = new OverlayService(overlayWindow, settingsService);
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

        _mainWindow = new MainWindow(settingsService, conversationSessionStore, _orchestrator, overlayService);
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
        _audioRecordingService?.Dispose();
        _audioRecordingService = null;
        _speechPlaybackService?.Dispose();
        _speechPlaybackService = null;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
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
        menu.Items.Add("Exit", null, async (_, _) => await ShutdownFromTrayAsync());

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => _mainWindow?.ShowFromTray();
    }

    public void ShowTrayBalloonTip(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, int timeoutMs = 3000)
    {
        if (_notifyIcon is null || !_notifyIcon.Visible)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(timeoutMs);
    }

    public Task ExitApplicationAsync()
    {
        return ShutdownFromTrayAsync();
    }

    public void ApplyTheme(string? themeName)
    {
        var dictionarySource = ResolveThemeDictionary(themeName);
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"{ThemeDictionaryPrefix}{dictionarySource}", UriKind.Relative)
        });
    }

    private async Task ShutdownFromTrayAsync()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
        }

        if (_mainWindow is not null)
        {
            _mainWindow.PrepareForExit();
        }

        if (_orchestrator is not null)
        {
            await DisposeAsyncWithTimeout(_orchestrator.DisposeAsync().AsTask(), TimeSpan.FromSeconds(5));
            _orchestrator = null;
        }

        if (_liveSessionService is not null)
        {
            await DisposeAsyncWithTimeout(_liveSessionService.DisposeAsync().AsTask(), TimeSpan.FromSeconds(5));
            _liveSessionService = null;
        }

        _audioRecordingService?.Dispose();
        _audioRecordingService = null;
        _speechPlaybackService?.Dispose();
        _speechPlaybackService = null;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_mainWindow is not null)
        {
            _mainWindow.Close();
            _mainWindow = null;
        }

        Shutdown();
    }

    private static async Task DisposeAsyncWithTimeout(Task disposeTask, TimeSpan timeout)
    {
        try
        {
            await disposeTask.WaitAsync(timeout);
        }
        catch
        {
        }
    }

    private static string ResolveThemeDictionary(string? themeName)
    {
        return themeName?.Trim().ToLowerInvariant() switch
        {
            "dark" => "DarkTheme.xaml",
            "light" => "LightTheme.xaml",
            _ => "SystemTheme.xaml"
        };
    }
}
