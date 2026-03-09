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
    private SettingsService? _settingsService;
    private ConversationSessionStore? _conversationSessionStore;
    private GameAssistantOrchestrator? _orchestrator;
    private GeminiLiveSessionService? _liveSessionService;
    private AudioRecordingService? _audioRecordingService;
    private SpeechPlaybackService? _speechPlaybackService;
    private DiagnosticsLogService? _diagnosticsLogService;
    private OverlayService? _overlayService;
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
        var diagnosticsLogsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinAiBuddy",
            "Logs");

        _settingsService = new SettingsService(settingsPath);
        _settingsService.EnsureLoaded();
        ApplyTheme(_settingsService.Current.AppTheme);
        _diagnosticsLogService = new DiagnosticsLogService(diagnosticsLogsPath);
        RegisterGlobalExceptionLogging();
        _diagnosticsLogService.LogApp("App", "Application startup.");
        _conversationSessionStore = new ConversationSessionStore(conversationSessionsPath);
        _conversationSessionStore.RecoverInterruptedSessions();

        var overlayWindow = new OverlayWindow();
        _overlayService = new OverlayService(overlayWindow, _settingsService);
        var screenCaptureService = new ScreenCaptureService();
        _audioRecordingService = new AudioRecordingService();
        _liveSessionService = new GeminiLiveSessionService(_diagnosticsLogService);
        _speechPlaybackService = new SpeechPlaybackService();

        _orchestrator = new GameAssistantOrchestrator(
            () => _settingsService.Current,
            screenCaptureService,
            _audioRecordingService,
            _liveSessionService,
            _overlayService,
            _speechPlaybackService);

        _mainWindow = CreateMainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Show();

        if (_settingsService.Current.StartMinimizedToTray)
        {
            _mainWindow.Hide();
        }

        ConfigureTrayIcon();
        _ = _mainWindow.StartLiveIfConfiguredAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _diagnosticsLogService?.LogApp("App", $"Application exit | code={e.ApplicationExitCode}");
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

    private void RegisterGlobalExceptionLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _diagnosticsLogService?.LogApp("Crash", $"DispatcherUnhandledException | handled={args.Handled}{Environment.NewLine}{args.Exception}");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            _diagnosticsLogService?.LogApp("Crash", $"AppDomainUnhandledException | terminating={args.IsTerminating}{Environment.NewLine}{args.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _diagnosticsLogService?.LogApp("Crash", $"UnobservedTaskException{Environment.NewLine}{args.Exception}");
        };
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

    public void ApplyTheme(string? themeName, bool refreshMainWindow = false)
    {
        var dictionarySource = ResolveThemeDictionary(themeName);
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"{ThemeDictionaryPrefix}{dictionarySource}", UriKind.Relative)
        });

        if (refreshMainWindow)
        {
            RefreshMainWindow();
        }
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

    private MainWindow CreateMainWindow()
    {
        var window = new MainWindow(
            _settingsService!,
            _conversationSessionStore!,
            _orchestrator!,
            _overlayService!,
            _diagnosticsLogService!);
        window.Icon = AppIconProvider.LoadWindowIcon();
        return window;
    }

    private void RefreshMainWindow()
    {
        if (_mainWindow is null || _settingsService is null || _conversationSessionStore is null || _orchestrator is null || _overlayService is null || _diagnosticsLogService is null)
        {
            return;
        }

        var previousWindow = _mainWindow;
        var snapshot = previousWindow.CaptureUiState();
        var wasVisible = previousWindow.IsVisible;

        var replacementWindow = CreateMainWindow();
        replacementWindow.RestoreUiState(snapshot);
        _mainWindow = replacementWindow;
        MainWindow = replacementWindow;

        if (wasVisible)
        {
            replacementWindow.Show();
            replacementWindow.Activate();
        }

        previousWindow.PrepareForExit();
        previousWindow.Close();
    }
}
