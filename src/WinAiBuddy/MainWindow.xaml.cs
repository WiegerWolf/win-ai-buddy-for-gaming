using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using WinAiBuddy.Models;
using WinAiBuddy.Services;
using WpfColor = System.Windows.Media.Color;

namespace WinAiBuddy;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly GameAssistantOrchestrator _orchestrator;
    private readonly OverlayService _overlayService;
    private readonly VoiceSamplePlayer _voiceSamplePlayer;
    private readonly MicrophoneLevelMonitor _microphoneLevelMonitor;
    private readonly DispatcherTimer _screenPreviewTimer;
    private bool _allowClose;
    private int _screenPreviewVersion;

    public MainWindow(
        SettingsService settingsService,
        GameAssistantOrchestrator orchestrator,
        OverlayService overlayService)
    {
        _settingsService = settingsService;
        _orchestrator = orchestrator;
        _overlayService = overlayService;
        _voiceSamplePlayer = new VoiceSamplePlayer(Path.Combine(AppContext.BaseDirectory, "Assets", "VoiceSamples"));
        _microphoneLevelMonitor = new MicrophoneLevelMonitor();
        _screenPreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };

        InitializeComponent();
        VoiceComboBox.ItemsSource = GeminiVoiceCatalog.All;
        ReloadCaptureSources();
        LoadSettings(_settingsService.Current);
        HookPreviewServices();
        RestartMicPreview();
        StartScreenPreviewTimer();

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
        ResumeCapturePreviews();
    }

    private void LoadSettings(AppSettings settings)
    {
        ApiKeyPasswordBox.Password = settings.ApiKey;
        LiveModelTextBox.Text = settings.LiveModel;
        SelectVoice(settings.Voice);
        SelectMicrophone(settings.MicrophoneDeviceName);
        SelectScreenSource(settings.ScreenDeviceName);
        MicrophoneGainSlider.Value = settings.MicrophoneGain;
        UpdateMicrophoneGainLabel(settings.MicrophoneGain);
        ScreenIntervalTextBox.Text = settings.ScreenCaptureIntervalMs.ToString();
        OverlaySecondsTextBox.Text = settings.OverlayDurationSeconds.ToString();
        OverlayOpacitySlider.Value = settings.OverlayOpacity;
        OverlayBgOpacitySlider.Value = settings.OverlayBackgroundOpacity;
        OverlayBgColorTextBox.Text = settings.OverlayBackgroundColor;
        OverlayFontSizeTextBox.Text = settings.OverlayFontSize.ToString("0.##", CultureInfo.InvariantCulture);
        OverlayTextColorTextBox.Text = settings.OverlayTextColor;
        OverlayOutlineColorTextBox.Text = settings.OverlayOutlineColor;
        OverlayOutlineThicknessTextBox.Text = settings.OverlayOutlineThickness.ToString("0.##", CultureInfo.InvariantCulture);
        UpdateOverlayOpacityLabel(settings.OverlayOpacity);
        OverlayBgOpacityValueTextBlock.Text = settings.OverlayBackgroundOpacity.ToString("0.00", CultureInfo.InvariantCulture);
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
            MicrophoneDeviceName = ReadSelectedMicrophone(current.MicrophoneDeviceName),
            MicrophoneGain = Math.Clamp(MicrophoneGainSlider.Value, 0.0, 3.0),
            ScreenDeviceName = ReadSelectedScreenSource(current.ScreenDeviceName),
            ScreenCaptureIntervalMs = ParseOrDefault(ScreenIntervalTextBox.Text, current.ScreenCaptureIntervalMs, 100, 5000),
            OverlayDurationSeconds = ParseOrDefault(OverlaySecondsTextBox.Text, current.OverlayDurationSeconds, 1, 60),
            OverlayOpacity = Math.Clamp(OverlayOpacitySlider.Value, 0.0, 1.0),
            OverlayBackgroundColor = NormalizeColorOrDefault(OverlayBgColorTextBox.Text, current.OverlayBackgroundColor),
            OverlayBackgroundOpacity = Math.Clamp(OverlayBgOpacitySlider.Value, 0.0, 1.0),
            OverlayFontSize = ParseDoubleOrDefault(OverlayFontSizeTextBox.Text, current.OverlayFontSize, 8, 144),
            OverlayTextColor = NormalizeColorOrDefault(OverlayTextColorTextBox.Text, current.OverlayTextColor),
            OverlayOutlineColor = NormalizeColorOrDefault(OverlayOutlineColorTextBox.Text, current.OverlayOutlineColor),
            OverlayOutlineThickness = ParseDoubleOrDefault(OverlayOutlineThicknessTextBox.Text, current.OverlayOutlineThickness, 0, 8),
            OverlayLeft = current.OverlayLeft,
            OverlayTop = current.OverlayTop,
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
            _overlayService.ApplySettings(settings);
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
            _overlayService.ApplySettings(settings);
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
        var settings = ReadSettingsFromUi();
        _settingsService.Save(settings);
        _overlayService.ApplySettings(settings);
        await _overlayService.ShowMessageAsync(
            "Overlay check: the assistant is ready to display advice over your game.",
            TimeSpan.FromSeconds(4));
    }

    private async void AdjustOverlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettingsFromUi();
            _settingsService.Save(settings);
            _overlayService.ApplySettings(settings);
            await _overlayService.BeginEditingAsync(
                "Drag me wherever you want live coaching to appear.");
            SetStatus("Drag the overlay to position it, then click Lock Overlay.");
        }
        catch (Exception ex)
        {
            SetStatus($"Adjust overlay failed: {ex.Message}");
        }
    }

    private async void LockOverlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        await _overlayService.EndEditingAsync();
        SetStatus("Overlay locked. Position saved.");
    }

    private async void ResetOverlayPositionButton_OnClick(object sender, RoutedEventArgs e)
    {
        var settings = ReadSettingsFromUi();
        settings.OverlayLeft = null;
        settings.OverlayTop = null;
        _settingsService.Save(settings);
        _overlayService.ApplySettings(settings);
        await _overlayService.ShowMessageAsync("Overlay reset to the default position.", TimeSpan.FromSeconds(2));
        SetStatus("Overlay position reset to bottom center.");
    }

    private void HideToTrayButton_OnClick(object sender, RoutedEventArgs e)
    {
        PauseCapturePreviews();
        Hide();
        SetStatus("Window hidden to tray.");
    }

    private void RefreshSourcesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var current = ReadSettingsFromUi();
        ReloadCaptureSources();
        SelectMicrophone(current.MicrophoneDeviceName);
        SelectScreenSource(current.ScreenDeviceName);
        RestartMicPreview();
        _ = RefreshScreenPreviewAsync();
        SetStatus("Audio and screen sources refreshed.");
    }

    private void MicrophoneGainSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
        {
            return;
        }

        var gain = Math.Clamp(e.NewValue, 0.0, 3.0);
        UpdateMicrophoneGainLabel(gain);
        _microphoneLevelMonitor.InputGain = gain;
        _orchestrator.UpdateMicrophoneGain(gain);
    }

    private void MicrophoneComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        RestartMicPreview();
    }

    private async void ScreenSourceComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        await RefreshScreenPreviewAsync();
    }

    private void OverlayOpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateOverlayOpacityLabel(e.NewValue);
        UpdateOverlayPreview();
    }

    private void OverlayBgOpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        OverlayBgOpacityValueTextBlock.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture);
        UpdateOverlayPreview();
    }

    private void OverlayPropertyTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateOverlayPreview();
    }

    private void UpdateOverlayPreview()
    {
        if (!IsInitialized) return;

        try
        {
            var settings = ReadSettingsFromUi();
            _overlayService.ApplySettings(settings);
        }
        catch { }
    }

    private void PickTextColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        PickColor(OverlayTextColorTextBox, "#FFFFFFFF");
    }

    private void PickBgColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        PickColor(OverlayBgColorTextBox, "#111111");
    }

    private void PickOutlineColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        PickColor(OverlayOutlineColorTextBox, "#FF000000");
    }

    private void PickColor(System.Windows.Controls.TextBox target, string fallback)
    {
        var hex = NormalizeColorOrDefault(target.Text, fallback);
        var wpfColor = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(hex);

        using var dialog = new System.Windows.Forms.ColorDialog();
        dialog.Color = System.Drawing.Color.FromArgb(wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B);

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dialog.Color;
            target.Text = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        }
    }

    private void FontSizeUpButton_OnClick(object sender, RoutedEventArgs e)
    {
        AdjustFontSize(2);
    }

    private void FontSizeDownButton_OnClick(object sender, RoutedEventArgs e)
    {
        AdjustFontSize(-2);
    }

    private void AdjustFontSize(double delta)
    {
        var current = ParseDoubleOrDefault(OverlayFontSizeTextBox.Text, _settingsService.Current.OverlayFontSize, 8, 144);
        var next = Math.Clamp(current + delta, 8, 144);
        OverlayFontSizeTextBox.Text = next.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void ThicknessUpButton_OnClick(object sender, RoutedEventArgs e)
    {
        AdjustThickness(0.5);
    }

    private void ThicknessDownButton_OnClick(object sender, RoutedEventArgs e)
    {
        AdjustThickness(-0.5);
    }

    private void AdjustThickness(double delta)
    {
        var current = ParseDoubleOrDefault(OverlayOutlineThicknessTextBox.Text, _settingsService.Current.OverlayOutlineThickness, 0, 8);
        var next = Math.Clamp(current + delta, 0, 8);
        OverlayOutlineThicknessTextBox.Text = next.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        PauseCapturePreviews();
        Hide();
        SetStatus("Still running in the tray. Use the tray icon to reopen or exit.");

        if (System.Windows.Application.Current is App app)
        {
            app.ShowTrayBalloonTip(
                "Still running in tray",
                "Win AI Buddy is still running here in the tray. Double-click the icon to reopen it or use Exit to quit.");
        }
    }

    private void Window_OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            PauseCapturePreviews();
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

    private static double ParseDoubleOrDefault(string? value, double fallback, double min, double max)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            !double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static string NormalizeColorOrDefault(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            var converted = System.Windows.Media.ColorConverter.ConvertFromString(value.Trim());
            return converted is WpfColor color ? color.ToString() : fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private void ReloadCaptureSources()
    {
        MicrophoneComboBox.ItemsSource = AudioRecordingService.GetInputDevices();
        ScreenSourceComboBox.ItemsSource = ScreenCaptureService.GetScreens();
    }

    private void HookPreviewServices()
    {
        _microphoneLevelMonitor.LevelChanged += level => Dispatcher.Invoke(() =>
        {
            MicrophoneLevelProgressBar.Value = Math.Round(level * 100, 0);
        });

        _microphoneLevelMonitor.StatusChanged += message => Dispatcher.Invoke(() =>
        {
            MicrophonePreviewStatusTextBlock.Text = message;
        });

        _screenPreviewTimer.Tick += async (_, _) => await RefreshScreenPreviewAsync();
    }

    private void ResumeCapturePreviews()
    {
        RestartMicPreview();
        StartScreenPreviewTimer();
    }

    private void PauseCapturePreviews()
    {
        _screenPreviewTimer.Stop();
        _microphoneLevelMonitor.Stop();
    }

    private void RestartMicPreview()
    {
        var preferredMic = ReadSelectedMicrophone(_settingsService.Current.MicrophoneDeviceName);
        var gain = Math.Clamp(MicrophoneGainSlider.Value, 0.0, 3.0);
        _microphoneLevelMonitor.Start(preferredMic, gain);
    }

    private void StartScreenPreviewTimer()
    {
        if (!_screenPreviewTimer.IsEnabled)
        {
            _screenPreviewTimer.Start();
        }

        _ = RefreshScreenPreviewAsync();
    }

    private async Task RefreshScreenPreviewAsync()
    {
        var previewRequest = Interlocked.Increment(ref _screenPreviewVersion);
        var selectedScreen = ReadSelectedScreenSource(_settingsService.Current.ScreenDeviceName);

        try
        {
            ScreenPreviewStatusTextBlock.Text = "Refreshing screen preview...";

            var bytes = await Task.Run(() =>
            {
                var capture = new ScreenCaptureService().CaptureMonitorJpeg(selectedScreen, 55);
                return capture.Bytes;
            });

            if (previewRequest != _screenPreviewVersion)
            {
                return;
            }

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            ScreenPreviewImage.Source = image;
            ScreenPreviewPlaceholderTextBlock.Visibility = Visibility.Collapsed;
            ScreenPreviewStatusTextBlock.Text = string.IsNullOrWhiteSpace(selectedScreen)
                ? "Showing the primary screen preview."
                : $"Showing preview for {selectedScreen}.";
        }
        catch (Exception ex)
        {
            if (previewRequest != _screenPreviewVersion)
            {
                return;
            }

            ScreenPreviewImage.Source = null;
            ScreenPreviewPlaceholderTextBlock.Visibility = Visibility.Visible;
            ScreenPreviewStatusTextBlock.Text = $"Screen preview unavailable: {ex.Message}";
        }
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

    private void SelectMicrophone(string deviceName)
    {
        var devices = (MicrophoneComboBox.ItemsSource as IEnumerable<AudioInputDeviceOption>)?.ToList()
            ?? AudioRecordingService.GetInputDevices().ToList();

        if (devices.Count == 0)
        {
            MicrophoneComboBox.SelectedItem = null;
            return;
        }

        var selected = devices.FirstOrDefault(device =>
            string.Equals(device.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? devices[0];

        MicrophoneComboBox.SelectedValue = selected.DeviceName;
    }

    private void SelectScreenSource(string deviceName)
    {
        var screens = (ScreenSourceComboBox.ItemsSource as IEnumerable<ScreenSourceOption>)?.ToList()
            ?? ScreenCaptureService.GetScreens().ToList();

        if (screens.Count == 0)
        {
            ScreenSourceComboBox.SelectedItem = null;
            return;
        }

        var selected = screens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? screens.FirstOrDefault(screen => screen.IsPrimary)
            ?? screens[0];

        ScreenSourceComboBox.SelectedValue = selected.DeviceName;
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

    private string ReadSelectedMicrophone(string fallback)
    {
        if (MicrophoneComboBox.SelectedValue is string selected && !string.IsNullOrWhiteSpace(selected))
        {
            return selected;
        }

        if (MicrophoneComboBox.SelectedItem is AudioInputDeviceOption option)
        {
            return option.DeviceName;
        }

        return fallback;
    }

    private string ReadSelectedScreenSource(string fallback)
    {
        if (ScreenSourceComboBox.SelectedValue is string selected && !string.IsNullOrWhiteSpace(selected))
        {
            return selected;
        }

        if (ScreenSourceComboBox.SelectedItem is ScreenSourceOption option)
        {
            return option.DeviceName;
        }

        return fallback;
    }

    private void UpdateOverlayOpacityLabel(double value)
    {
        OverlayOpacityValueTextBlock.Text = value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void UpdateMicrophoneGainLabel(double gain)
    {
        MicrophoneGainValueTextBlock.Text = $"{Math.Round(gain * 100):0}%";
    }

    protected override void OnClosed(EventArgs e)
    {
        _screenPreviewTimer.Stop();
        _microphoneLevelMonitor.Dispose();
        _voiceSamplePlayer.Dispose();
        base.OnClosed(e);
    }
}
