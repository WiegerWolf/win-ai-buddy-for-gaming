using System.ComponentModel;
using System.IO;
using System.Windows;
using WinAiBuddy.Models;
using WinAiBuddy.Services;

namespace WinAiBuddy;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly GameAssistantOrchestrator _orchestrator;
    private readonly OverlayService _overlayService;
    private readonly VoiceSamplePlayer _voiceSamplePlayer;
    private bool _allowClose;

    public MainWindow(
        SettingsService settingsService,
        GameAssistantOrchestrator orchestrator,
        OverlayService overlayService)
    {
        _settingsService = settingsService;
        _orchestrator = orchestrator;
        _overlayService = overlayService;
        _voiceSamplePlayer = new VoiceSamplePlayer(Path.Combine(AppContext.BaseDirectory, "Assets", "VoiceSamples"));

        InitializeComponent();
        VoiceComboBox.ItemsSource = GeminiVoiceCatalog.All;
        LoadSettings(_settingsService.Current);

        _orchestrator.StatusChanged += message => Dispatcher.Invoke(() => SetStatus(message));
        _orchestrator.SessionStateChanged += isRunning => Dispatcher.Invoke(() =>
        {
            SessionStateTextBlock.Text = isRunning ? "Running" : "Stopped";
        });
        _orchestrator.InputTranscriptionChanged += text => Dispatcher.Invoke(() =>
        {
            InputTranscriptTextBlock.Text = string.IsNullOrWhiteSpace(text) ? "Nothing heard yet." : text;
        });
        _orchestrator.OutputTranscriptionChanged += text => Dispatcher.Invoke(() =>
        {
            OutputTranscriptTextBlock.Text = string.IsNullOrWhiteSpace(text) ? "No model response yet." : text;
        });
    }

    public async Task StartLiveIfConfiguredAsync()
    {
        if (_settingsService.Current.AutoStartSession)
        {
            await _orchestrator.StartLiveSessionAsync();
        }
    }

    public void PrepareForExit()
    {
        _allowClose = true;
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void LoadSettings(AppSettings settings)
    {
        ApiKeyPasswordBox.Password = settings.ApiKey;
        LiveModelTextBox.Text = settings.LiveModel;
        SelectVoice(settings.Voice);
        ScreenIntervalTextBox.Text = settings.ScreenCaptureIntervalMs.ToString();
        OverlaySecondsTextBox.Text = settings.OverlayDurationSeconds.ToString();
        SystemPromptTextBox.Text = settings.SystemPrompt;
        StreamScreenFramesCheckBox.IsChecked = settings.StreamScreenFrames;
        AutoStartSessionCheckBox.IsChecked = settings.AutoStartSession;
        StartMinimizedCheckBox.IsChecked = settings.StartMinimizedToTray;
    }

    private AppSettings ReadSettingsFromUi()
    {
        var current = _settingsService.Current;

        return new AppSettings
        {
            ApiKey = ApiKeyPasswordBox.Password.Trim(),
            LiveModel = string.IsNullOrWhiteSpace(LiveModelTextBox.Text) ? current.LiveModel : LiveModelTextBox.Text.Trim(),
            Voice = ReadSelectedVoice(current.Voice),
            ScreenCaptureIntervalMs = ParseOrDefault(ScreenIntervalTextBox.Text, current.ScreenCaptureIntervalMs, 100, 5000),
            OverlayDurationSeconds = ParseOrDefault(OverlaySecondsTextBox.Text, current.OverlayDurationSeconds, 1, 60),
            ScreenshotJpegQuality = current.ScreenshotJpegQuality,
            AutoStartSession = AutoStartSessionCheckBox.IsChecked == true,
            StreamScreenFrames = StreamScreenFramesCheckBox.IsChecked == true,
            StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true,
            SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptTextBox.Text) ? current.SystemPrompt : SystemPromptTextBox.Text.Trim()
        };
    }

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettingsFromUi();
            _settingsService.Save(settings);
            SetStatus($"Saved settings. Model: {settings.LiveModel}. Voice: {settings.Voice}");
            await _overlayService.ShowMessageAsync("Settings saved.", TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
        }
    }

    private async void StartLiveButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettingsFromUi();
            _settingsService.Save(settings);
            SetStatus("Starting live session...");
            await _orchestrator.StartLiveSessionAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Start failed: {ex.Message}");
        }
    }

    private async void StopLiveButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("Stopping live session...");
            await _orchestrator.StopLiveSessionAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Stop failed: {ex.Message}");
        }
    }

    private void PreviewVoiceButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var voice = ReadSelectedVoice(_settingsService.Current.Voice);
            _voiceSamplePlayer.Play(voice);
            VoicePreviewStatusTextBlock.Text = $"Playing local sample for {voice}.";
        }
        catch (Exception ex)
        {
            VoicePreviewStatusTextBlock.Text = $"Preview failed: {ex.Message}";
        }
    }

    private void StopPreviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        _voiceSamplePlayer.Stop();
        VoicePreviewStatusTextBlock.Text = "Voice preview stopped.";
    }

    private async void OverlayTestButton_OnClick(object sender, RoutedEventArgs e)
    {
        await _overlayService.ShowMessageAsync(
            "Overlay check: the assistant is ready to display advice over your game.",
            TimeSpan.FromSeconds(4));
    }

    private void HideToTrayButton_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
        SetStatus("Window hidden to tray.");
    }

    private void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        SetStatus("Still running in the tray. Use the tray icon to reopen or exit.");
    }

    private void Window_OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            SetStatus("Window minimized to tray.");
        }
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

    private static int ParseOrDefault(string? value, int fallback, int min, int max)
    {
        if (!int.TryParse(value, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, min, max);
    }

    private void SelectVoice(string voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            VoiceComboBox.SelectedValue = "Kore";
            return;
        }

        var existing = GeminiVoiceCatalog.All.FirstOrDefault(option =>
            string.Equals(option.Name, voiceName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new GeminiVoiceOption(voiceName, "Custom");
            var items = GeminiVoiceCatalog.All.ToList();
            items.Add(existing);
            VoiceComboBox.ItemsSource = items;
        }

        VoiceComboBox.SelectedValue = existing.Name;
    }

    private string ReadSelectedVoice(string fallback)
    {
        if (VoiceComboBox.SelectedValue is string selected && !string.IsNullOrWhiteSpace(selected))
        {
            return selected;
        }

        if (VoiceComboBox.SelectedItem is GeminiVoiceOption option)
        {
            return option.Name;
        }

        return fallback;
    }

    protected override void OnClosed(EventArgs e)
    {
        _voiceSamplePlayer.Dispose();
        base.OnClosed(e);
    }
}
