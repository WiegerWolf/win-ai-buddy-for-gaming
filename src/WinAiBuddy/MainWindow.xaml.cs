using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly ConversationSessionStore _conversationSessionStore;
    private readonly GameAssistantOrchestrator _orchestrator;
    private readonly OverlayService _overlayService;
    private readonly VoiceSamplePlayer _voiceSamplePlayer;
    private readonly MicrophoneLevelMonitor _microphoneLevelMonitor;
    private readonly DispatcherTimer _screenPreviewTimer;
    private readonly ObservableCollection<ConversationLogEntry> _conversationLogEntries = new();
    private readonly ObservableCollection<ConversationSessionSummary> _conversationSessions = new();
    private bool _allowClose;
    private bool _isSessionRunning;
    private bool _isChangingSessionSelection;
    private int _screenPreviewVersion;
    private string? _activeConversationSessionId;
    private string? _selectedConversationSessionId;
    private ConversationSessionRecord? _pendingResumeSession;
    private ConversationLogEntry? _latestUserLogEntry;
    private ConversationLogEntry? _latestModelLogEntry;

    public MainWindow(
        SettingsService settingsService,
        ConversationSessionStore conversationSessionStore,
        GameAssistantOrchestrator orchestrator,
        OverlayService overlayService)
    {
        _settingsService = settingsService;
        _conversationSessionStore = conversationSessionStore;
        _orchestrator = orchestrator;
        _overlayService = overlayService;
        _voiceSamplePlayer = new VoiceSamplePlayer(Path.Combine(AppContext.BaseDirectory, "Assets", "VoiceSamples"));
        _microphoneLevelMonitor = new MicrophoneLevelMonitor();
        _screenPreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };

        InitializeComponent();
        System.Windows.DataObject.AddPastingHandler(ScreenIntervalTextBox, ScreenIntervalTextBox_OnPasting);
        VoiceComboBox.ItemsSource = GeminiVoiceCatalog.All;
        InitializeModelOptionLists();
        ConversationSessionsListBox.ItemsSource = _conversationSessions;
        LogsListBox.ItemsSource = _conversationLogEntries;
        ReloadCaptureSources();
        LoadSettings(_settingsService.Current);
        HookPreviewServices();
        RestartMicPreview();
        StartScreenPreviewTimer();
        LoadConversationSessions();
        UpdateLogsPlaceholderVisibility();
        UpdateSessionButtons(isRunning: false);
        UpdateConversationButtons();

        _orchestrator.StatusChanged += message => Dispatcher.Invoke(() => SetStatus(message));
        _orchestrator.SessionStateChanged += isRunning => Dispatcher.Invoke(() =>
        {
            var wasRunning = _isSessionRunning;
            _isSessionRunning = isRunning;
            SessionStateTextBlock.Text = isRunning ? "Running" : "Stopped";
            SessionStatusPill.Background = isRunning
                ? new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString("#3300D4AA"))
                : new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString("#22C62828"));
            UpdateSessionButtons(isRunning);
            UpdateConversationButtons();

            if (isRunning && !wasRunning)
            {
                EnsureConversationSessionStarted();
            }
            else if (!isRunning && wasRunning)
            {
                FinalizeActiveConversationSession();
            }
        });
        _orchestrator.InputTranscriptionChanged += text => Dispatcher.Invoke(() =>
        {
            InputTranscriptTextBlock.Text = string.IsNullOrWhiteSpace(text) ? "Nothing heard yet." : text;
            LogConversationUpdate("You", text, isModel: false);
        });
        _orchestrator.OutputTranscriptionChanged += text => Dispatcher.Invoke(() =>
        {
            OutputTranscriptTextBlock.Text = string.IsNullOrWhiteSpace(text) ? "No model response yet." : text;
            LogConversationUpdate("Gemini", text, isModel: true);
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

    private void LoadConversationSessions()
    {
        RefreshConversationSessionSummaries();

        if (_conversationSessions.Count == 0)
        {
            _selectedConversationSessionId = null;
            _activeConversationSessionId = null;
            ConversationSessionHeaderTextBlock.Text = "No saved sessions yet";
            ConversationSessionMetaTextBlock.Text = "Start a live session and the transcript will be saved here automatically.";
            ClearConversationView();
            UpdateConversationButtons();
            return;
        }

        var preferredSessionId = _activeConversationSessionId ?? _selectedConversationSessionId ?? _conversationSessions[0].Id;
        SelectConversationSessionById(preferredSessionId, fallbackToFirst: true);
    }

    private void RefreshConversationSessionSummaries()
    {
        var sessions = _conversationSessionStore.ListSessions();
        var preferredSessionId = _activeConversationSessionId ?? _selectedConversationSessionId;

        _conversationSessions.Clear();

        foreach (var session in sessions)
        {
            _conversationSessions.Add(session);
        }

        UpdateConversationSessionsPlaceholderVisibility();

        _isChangingSessionSelection = true;
        ConversationSessionsListBox.SelectedItem = string.IsNullOrWhiteSpace(preferredSessionId)
            ? null
            : _conversationSessions.FirstOrDefault(item =>
                string.Equals(item.Id, preferredSessionId, StringComparison.OrdinalIgnoreCase));
        _isChangingSessionSelection = false;
    }

    private void EnsureConversationSessionStarted()
    {
        if (!string.IsNullOrWhiteSpace(_activeConversationSessionId))
        {
            return;
        }

        var record = _conversationSessionStore.StartSession(
            ReadSelectedLiveModel(_settingsService.Current.LiveModel),
            _pendingResumeSession?.Entries,
            _pendingResumeSession?.Id);
        _activeConversationSessionId = record.Id;
        _selectedConversationSessionId = record.Id;
        _pendingResumeSession = null;
        LoadConversationSessions();
        PopulateConversationView(record);
        SetStatus("Live session started. Conversation history is being saved.");
    }

    private void FinalizeActiveConversationSession()
    {
        if (string.IsNullOrWhiteSpace(_activeConversationSessionId))
        {
            return;
        }

        _conversationSessionStore.SaveSnapshot(_activeConversationSessionId, _conversationLogEntries);
        _conversationSessionStore.EndSession(_activeConversationSessionId);
        _activeConversationSessionId = null;
        _pendingResumeSession = null;
        LoadConversationSessions();
    }

    private void ClearConversationView()
    {
        _conversationLogEntries.Clear();
        _latestUserLogEntry = null;
        _latestModelLogEntry = null;
        UpdateLogsPlaceholderVisibility();
    }

    private void SelectConversationSessionById(string? sessionId, bool fallbackToFirst = false)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            if (fallbackToFirst && _conversationSessions.Count > 0)
            {
                SelectConversationSessionById(_conversationSessions[0].Id);
            }

            return;
        }

        var summary = _conversationSessions.FirstOrDefault(item =>
            string.Equals(item.Id, sessionId, StringComparison.OrdinalIgnoreCase));

        if (summary is null)
        {
            if (fallbackToFirst && _conversationSessions.Count > 0)
            {
                SelectConversationSessionById(_conversationSessions[0].Id);
            }

            return;
        }

        var record = _conversationSessionStore.GetSession(summary.Id);
        if (record is null)
        {
            return;
        }

        _isChangingSessionSelection = true;
        ConversationSessionsListBox.SelectedItem = summary;
        _isChangingSessionSelection = false;

        _selectedConversationSessionId = summary.Id;
        PopulateConversationView(record);
        UpdateConversationButtons();
    }

    private void PopulateConversationView(ConversationSessionRecord record)
    {
        ClearConversationView();

        foreach (var entry in record.Entries)
        {
            var logEntry = new ConversationLogEntry(entry.Timestamp.ToLocalTime(), entry.Role, entry.Text);
            _conversationLogEntries.Add(logEntry);

            if (string.Equals(entry.Role, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                _latestModelLogEntry = logEntry;
            }
            else if (string.Equals(entry.Role, "You", StringComparison.OrdinalIgnoreCase))
            {
                _latestUserLogEntry = logEntry;
            }
        }

        ConversationSessionHeaderTextBlock.Text = record.StartedAt.ToLocalTime().ToString("dddd, MMM d - HH:mm");
        var status = string.IsNullOrWhiteSpace(record.Status) ? "Saved" : record.Status;
        var resumedText = string.IsNullOrWhiteSpace(record.ResumedFromSessionId) ? string.Empty : " - continued";
        var endedText = record.EndedAt is null
            ? "still open"
            : $"ended {record.EndedAt.Value.ToLocalTime():HH:mm}";
        ConversationSessionMetaTextBlock.Text =
            $"{status}{resumedText} - {record.Entries.Count} {(record.Entries.Count == 1 ? "message" : "messages")} - {endedText}";

        UpdateLogsPlaceholderVisibility();
        ScrollLogsToEnd();
    }

    private void LoadSettings(AppSettings settings)
    {
        SelectAppTheme(settings.AppTheme);
        ApiKeyPasswordBox.Password = settings.ApiKey;
        SelectLiveModel(settings.LiveModel);
        SelectMediaResolution(settings.MediaResolution);
        EnableAffectiveDialogCheckBox.IsChecked = settings.EnableAffectiveDialog;
        EnableProactiveAudioCheckBox.IsChecked = settings.EnableProactiveAudio;
        EnableContextCompressionCheckBox.IsChecked = settings.EnableContextWindowCompression;
        ContextCompressionTriggerTokensTextBox.Text = settings.ContextCompressionTriggerTokens.ToString(CultureInfo.InvariantCulture);
        ContextCompressionTargetTokensTextBox.Text = settings.ContextCompressionTargetTokens.ToString(CultureInfo.InvariantCulture);
        SelectThinkingMode(settings);
        ThinkingBudgetTextBox.Text = settings.ThinkingBudget.ToString(CultureInfo.InvariantCulture);
        SelectThinkingLevel(settings.ThinkingLevel);
        IncludeThoughtsCheckBox.IsChecked = settings.IncludeThoughts;
        SelectVoice(settings.Voice);
        SelectMicrophone(settings.MicrophoneDeviceName);
        SelectScreenSource(settings.ScreenDeviceName);
        MicrophoneGainSlider.Value = settings.MicrophoneGain;
        UpdateMicrophoneGainLabel(settings.MicrophoneGain);
        ScreenIntervalTextBox.Text = settings.ScreenCaptureIntervalMs.ToString();
        UpdateScreenPreviewTimerInterval();
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
        UpdateOverlayColorSwatches();
        UpdateThinkingControlsState();
        SystemPromptTextBox.Text = settings.SystemPrompt;
        StreamScreenFramesCheckBox.IsChecked = settings.StreamScreenFrames;
        AutoStartSessionCheckBox.IsChecked = settings.AutoStartSession;
        StartMinimizedCheckBox.IsChecked = settings.StartMinimizedToTray;
    }

    private AppSettings ReadSettingsFromUi()
    {
        var current = _settingsService.Current;
        var thinkingMode = ReadSelectedThinkingMode(current);
        var isThinkingCustom = string.Equals(thinkingMode, "Custom", StringComparison.OrdinalIgnoreCase);
        var isThinkingDisabled = string.Equals(thinkingMode, "Disabled", StringComparison.OrdinalIgnoreCase);

        return new AppSettings
        {
            AppTheme = ReadSelectedAppTheme(current.AppTheme),
            ApiKey = ApiKeyPasswordBox.Password.Trim(),
            LiveModel = ReadSelectedLiveModel(current.LiveModel),
            EnableAffectiveDialog = EnableAffectiveDialogCheckBox.IsChecked == true,
            EnableProactiveAudio = EnableProactiveAudioCheckBox.IsChecked == true,
            EnableContextWindowCompression = EnableContextCompressionCheckBox.IsChecked == true,
            ContextCompressionTriggerTokens = ParseOrDefault(
                ContextCompressionTriggerTokensTextBox.Text,
                current.ContextCompressionTriggerTokens,
                1,
                1_000_000),
            ContextCompressionTargetTokens = ParseOrDefault(
                ContextCompressionTargetTokensTextBox.Text,
                current.ContextCompressionTargetTokens,
                1,
                1_000_000),
            MediaResolution = ReadSelectedMediaResolution(current.MediaResolution),
            EnableThinkingConfig = !string.Equals(thinkingMode, "Default", StringComparison.OrdinalIgnoreCase),
            ThinkingMode = thinkingMode,
            ThinkingBudget = isThinkingDisabled
                ? 0
                : (isThinkingCustom
                    ? ParseThinkingBudgetOrDefault(ThinkingBudgetTextBox.Text, current.ThinkingBudget)
                    : -1),
            ThinkingLevel = isThinkingCustom
                ? ReadSelectedThinkingLevel(current.ThinkingLevel)
                : "Default",
            IncludeThoughts = isThinkingCustom && IncludeThoughtsCheckBox.IsChecked == true,
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
            var previousTheme = _settingsService.Current.AppTheme;
            var settings = ReadSettingsFromUi();
            _settingsService.Save(settings);
            _overlayService.ApplySettings(settings);
            var themeChanged = !string.Equals(previousTheme, settings.AppTheme, StringComparison.OrdinalIgnoreCase);
            SetStatus(themeChanged
                ? $"Saved settings. Model: {settings.LiveModel}. Theme change will apply next time you open the app."
                : $"Saved settings. Model: {settings.LiveModel}. Voice: {settings.Voice}");
            await _overlayService.ShowMessageAsync(
                themeChanged ? "Settings saved. Restart the app to switch theme." : "Settings saved.",
                TimeSpan.FromSeconds(2));
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
            _pendingResumeSession = null;
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

    private async void ResumeSessionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isSessionRunning)
        {
            SetStatus("Stop the current live session before resuming an older one.");
            return;
        }

        var sessionToResume = GetSelectedConversationSession();
        if (sessionToResume is null)
        {
            SetStatus("Select a saved conversation session first.");
            return;
        }

        if (sessionToResume.Entries.Count == 0)
        {
            SetStatus("That saved session has no conversation turns to resume.");
            return;
        }

        try
        {
            _pendingResumeSession = sessionToResume;
            var settings = ReadSettingsFromUi();
            _settingsService.Save(settings);
            _overlayService.ApplySettings(settings);
            SetStatus("Starting live session from the selected saved conversation...");
            await _orchestrator.StartLiveSessionAsync(sessionToResume.Entries);
        }
        catch (Exception ex)
        {
            _pendingResumeSession = null;
            SetStatus($"Resume failed: {ex.Message}");
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

    private async void ExitAppButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            await app.ExitApplicationAsync();
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

    private void ConversationSessionsListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isChangingSessionSelection || ConversationSessionsListBox.SelectedItem is not ConversationSessionSummary summary)
        {
            UpdateConversationButtons();
            return;
        }

        if (_isSessionRunning &&
            !string.IsNullOrWhiteSpace(_activeConversationSessionId) &&
            !string.Equals(summary.Id, _activeConversationSessionId, StringComparison.OrdinalIgnoreCase))
        {
            _isChangingSessionSelection = true;
            ConversationSessionsListBox.SelectedItem = _conversationSessions.FirstOrDefault(item =>
                string.Equals(item.Id, _activeConversationSessionId, StringComparison.OrdinalIgnoreCase));
            _isChangingSessionSelection = false;
            SetStatus("Stop the current live session to browse older saved conversations.");
            UpdateConversationButtons();
            return;
        }

        SelectConversationSessionById(summary.Id);
        UpdateConversationButtons();
    }

    private void CopyLogsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_conversationLogEntries.Count == 0)
        {
            SetStatus("There are no logs to copy yet.");
            return;
        }

        var text = string.Join(
            Environment.NewLine + Environment.NewLine,
            _conversationLogEntries.Select(entry =>
                $"[{entry.DisplayTimestamp}] {entry.Role}{Environment.NewLine}{entry.Text}"));

        System.Windows.Clipboard.SetText(text);
        SetStatus("Copied conversation logs to the clipboard.");
    }

    private void ClearLogsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedConversationSessionId))
        {
            SetStatus("There are no saved conversation logs to clear.");
            return;
        }

        _conversationSessionStore.ClearSessionEntries(_selectedConversationSessionId);
        ClearConversationView();
        LoadConversationSessions();
        UpdateConversationButtons();
        SetStatus("Cleared the selected conversation session.");
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

    private void ScreenIntervalTextBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
    }

    private void ScreenIntervalTextBox_OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.SourceDataObject.GetDataPresent(System.Windows.DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var text = e.SourceDataObject.GetData(System.Windows.DataFormats.Text) as string;
        if (string.IsNullOrWhiteSpace(text) || !Regex.IsMatch(text, "^[0-9]+$"))
        {
            e.CancelCommand();
        }
    }

    private void ScreenIntervalTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateScreenPreviewTimerInterval();
    }

    private void ScreenIntervalUpButton_OnClick(object sender, RoutedEventArgs e)
    {
        AdjustScreenInterval(100);
    }

    private void ScreenIntervalDownButton_OnClick(object sender, RoutedEventArgs e)
    {
        AdjustScreenInterval(-100);
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
        UpdateOverlayColorSwatches();
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

    private void LogConversationUpdate(string role, string? text, bool isModel)
    {
        if (string.IsNullOrWhiteSpace(_activeConversationSessionId))
        {
            return;
        }

        var cleaned = text?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return;
        }

        var latestEntry = isModel ? _latestModelLogEntry : _latestUserLogEntry;

        if (latestEntry is not null)
        {
            if (string.Equals(latestEntry.Text, cleaned, StringComparison.Ordinal))
            {
                return;
            }

            if (ReferenceEquals(_conversationLogEntries.LastOrDefault(), latestEntry) &&
                cleaned.StartsWith(latestEntry.Text, StringComparison.Ordinal))
            {
                latestEntry.Text = cleaned;
                latestEntry.Timestamp = DateTime.Now;
                PersistConversationSnapshot();
                ScrollLogsToEnd();
                return;
            }
        }

        var entry = new ConversationLogEntry(DateTime.Now, role, cleaned);
        _conversationLogEntries.Add(entry);

        if (_conversationLogEntries.Count > 500)
        {
            var removed = _conversationLogEntries[0];
            _conversationLogEntries.RemoveAt(0);

            if (ReferenceEquals(removed, _latestUserLogEntry))
            {
                _latestUserLogEntry = null;
            }

            if (ReferenceEquals(removed, _latestModelLogEntry))
            {
                _latestModelLogEntry = null;
            }
        }

        if (isModel)
        {
            _latestModelLogEntry = entry;
        }
        else
        {
            _latestUserLogEntry = entry;
        }

        UpdateLogsPlaceholderVisibility();
        PersistConversationSnapshot();
        ScrollLogsToEnd();
    }

    private void PersistConversationSnapshot()
    {
        if (string.IsNullOrWhiteSpace(_activeConversationSessionId))
        {
            return;
        }

        _conversationSessionStore.SaveSnapshot(_activeConversationSessionId, _conversationLogEntries);
        RefreshConversationSessionSummaries();
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
            UpdateOverlayColorSwatches();
        }
    }

    private void AdjustScreenInterval(int delta)
    {
        var current = ReadScreenIntervalMs(_settingsService.Current.ScreenCaptureIntervalMs);
        var next = Math.Clamp(current + delta, 100, 5000);
        ScreenIntervalTextBox.Text = next.ToString(CultureInfo.InvariantCulture);
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

    private ConversationSessionRecord? GetSelectedConversationSession()
    {
        return string.IsNullOrWhiteSpace(_selectedConversationSessionId)
            ? null
            : _conversationSessionStore.GetSession(_selectedConversationSessionId);
    }

    private void InitializeModelOptionLists()
    {
        AppThemeComboBox.ItemsSource = new[]
        {
            "System",
            "Light",
            "Dark"
        };

        LiveModelComboBox.ItemsSource = new[]
        {
            "gemini-2.5-flash-native-audio-preview-12-2025"
        };

        MediaResolutionComboBox.ItemsSource = new[]
        {
            "Default",
            "Low",
            "Medium",
            "High"
        };

        ThinkingLevelComboBox.ItemsSource = new[]
        {
            "Default",
            "Minimal",
            "Low",
            "Medium",
            "High"
        };

        ThinkingModeComboBox.ItemsSource = new[]
        {
            "Default",
            "Disabled",
            "Custom"
        };
    }

    private static int ParseOrDefault(string? value, int fallback, int min, int max)
    {
        if (!int.TryParse(value, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static int ParseThinkingBudgetOrDefault(string? value, int fallback)
    {
        if (!int.TryParse(value, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, -1, 1_000_000);
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
        UpdateScreenPreviewTimerInterval();

        if (!_screenPreviewTimer.IsEnabled)
        {
            _screenPreviewTimer.Start();
        }

        _ = RefreshScreenPreviewAsync();
    }

    private void UpdateScreenPreviewTimerInterval()
    {
        var intervalMs = ReadScreenIntervalMs(_settingsService.Current.ScreenCaptureIntervalMs);
        _screenPreviewTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
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

    private void SelectAppTheme(string value)
    {
        var selected = string.IsNullOrWhiteSpace(value) ? "System" : value.Trim();
        if (!AppThemeComboBox.Items.Cast<string>()
                .Any(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase)))
        {
            selected = "System";
        }

        AppThemeComboBox.SelectedItem = AppThemeComboBox.Items.Cast<string>()
            .First(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectLiveModel(string value)
    {
        var selected = string.IsNullOrWhiteSpace(value)
            ? "gemini-2.5-flash-native-audio-preview-12-2025"
            : value.Trim();

        if (!LiveModelComboBox.Items.Cast<string>()
                .Any(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase)))
        {
            selected = "gemini-2.5-flash-native-audio-preview-12-2025";
        }

        LiveModelComboBox.SelectedItem = LiveModelComboBox.Items.Cast<string>()
            .First(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectMediaResolution(string value)
    {
        var selected = string.IsNullOrWhiteSpace(value) ? "Default" : value.Trim();
        if (!MediaResolutionComboBox.Items.Cast<string>()
                .Any(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase)))
        {
            selected = "Default";
        }

        MediaResolutionComboBox.SelectedItem = MediaResolutionComboBox.Items.Cast<string>()
            .First(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectThinkingMode(AppSettings settings)
    {
        var selected = !string.IsNullOrWhiteSpace(settings.ThinkingMode)
            ? settings.ThinkingMode.Trim()
            : settings.EnableThinkingConfig
                ? settings.ThinkingBudget == 0 ? "Disabled" : "Custom"
                : "Default";

        if (!ThinkingModeComboBox.Items.Cast<string>()
                .Any(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase)))
        {
            selected = "Default";
        }

        ThinkingModeComboBox.SelectedItem = ThinkingModeComboBox.Items.Cast<string>()
            .First(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectThinkingLevel(string value)
    {
        var selected = string.IsNullOrWhiteSpace(value) ? "Default" : value.Trim();
        if (!ThinkingLevelComboBox.Items.Cast<string>()
                .Any(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase)))
        {
            selected = "Default";
        }

        ThinkingLevelComboBox.SelectedItem = ThinkingLevelComboBox.Items.Cast<string>()
            .First(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase));
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

    private string ReadSelectedAppTheme(string fallback)
    {
        if (AppThemeComboBox.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
        {
            return selected;
        }

        return fallback;
    }

    private string ReadSelectedLiveModel(string fallback)
    {
        if (LiveModelComboBox.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
        {
            return selected;
        }

        return fallback;
    }

    private string ReadSelectedMediaResolution(string fallback)
    {
        if (MediaResolutionComboBox.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
        {
            return selected;
        }

        return fallback;
    }

    private string ReadSelectedThinkingMode(AppSettings fallback)
    {
        if (ThinkingModeComboBox.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
        {
            return selected;
        }

        return string.IsNullOrWhiteSpace(fallback.ThinkingMode) ? "Default" : fallback.ThinkingMode;
    }

    private string ReadSelectedThinkingLevel(string fallback)
    {
        if (ThinkingLevelComboBox.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
        {
            return selected;
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

    private void UpdateOverlayColorSwatches()
    {
        SetColorSwatch(OverlayBgColorSwatch, OverlayBgColorTextBox.Text, "#111111");
        SetColorSwatch(OverlayTextColorSwatch, OverlayTextColorTextBox.Text, "#FFFFFFFF");
        SetColorSwatch(OverlayOutlineColorSwatch, OverlayOutlineColorTextBox.Text, "#FF06131A");
    }

    private static void SetColorSwatch(Border swatch, string? value, string fallback)
    {
        var hex = NormalizeColorOrDefault(value, fallback);
        swatch.Background = new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    }

    private void UpdateMicrophoneGainLabel(double gain)
    {
        MicrophoneGainValueTextBlock.Text = $"{Math.Round(gain * 100):0}%";
    }

    private void UpdateSessionButtons(bool isRunning)
    {
        StartSessionButton.IsEnabled = !isRunning;
        StopSessionButton.IsEnabled = isRunning;
    }

    private void UpdateConversationButtons()
    {
        if (!IsInitialized)
        {
            return;
        }

        var hasSelectedSession = !string.IsNullOrWhiteSpace(_selectedConversationSessionId);
        ResumeSessionButton.IsEnabled = !_isSessionRunning && hasSelectedSession;
        ClearLogsButton.IsEnabled = hasSelectedSession;
        CopyLogsButton.IsEnabled = _conversationLogEntries.Count > 0;
    }

    private void UpdateLogsPlaceholderVisibility()
    {
        LogsPlaceholderTextBlock.Visibility = _conversationLogEntries.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateConversationButtons();
    }

    private void UpdateConversationSessionsPlaceholderVisibility()
    {
        ConversationSessionsPlaceholderTextBlock.Visibility = _conversationSessions.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ScrollLogsToEnd()
    {
        if (_conversationLogEntries.Count == 0)
        {
            return;
        }

        var latest = _conversationLogEntries[^1];
        LogsListBox.ScrollIntoView(latest);
        UpdateLogsPlaceholderVisibility();
    }

    private int ReadScreenIntervalMs(int fallback)
    {
        return ParseOrDefault(ScreenIntervalTextBox.Text, fallback, 100, 5000);
    }

    private void NavSession_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        SessionPanel.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;
        ConversationPanel.Visibility = Visibility.Collapsed;
    }

    private void NavSettings_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        SessionPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        ConversationPanel.Visibility = Visibility.Collapsed;
    }

    private void NavConversation_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        SessionPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        ConversationPanel.Visibility = Visibility.Visible;
    }

    private void OpenSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        NavSettings.IsChecked = true;
    }

    private void OpenConversationButton_OnClick(object sender, RoutedEventArgs e)
    {
        NavConversation.IsChecked = true;
    }

    private void ThinkingModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateThinkingControlsState();
    }

    private void UpdateThinkingControlsState()
    {
        var mode = ThinkingModeComboBox.SelectedItem as string ?? "Default";
        var isCustom = string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase);
        var isDisabled = string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase);

        ThinkingCustomOptionsPanel.IsEnabled = isCustom;
        ThinkingCustomOptionsPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

        ThinkingModeDescriptionTextBlock.Text = isDisabled
            ? "Thinking is explicitly turned off. The app sends a thinking budget of 0 to Gemini."
            : isCustom
                ? "Customize the model's thinking budget and level for this session."
                : "Use the model default thinking behavior.";
    }

    protected override void OnClosed(EventArgs e)
    {
        _screenPreviewTimer.Stop();
        _microphoneLevelMonitor.Dispose();
        _voiceSamplePlayer.Dispose();
        base.OnClosed(e);
    }
}
