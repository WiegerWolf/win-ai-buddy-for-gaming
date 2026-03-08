using WinAiBuddy.Models;

namespace WinAiBuddy.Services;

public sealed class GameAssistantOrchestrator : IAsyncDisposable
{
    private readonly Func<AppSettings> _settingsProvider;
    private readonly ScreenCaptureService _screenCaptureService;
    private readonly AudioRecordingService _audioRecordingService;
    private readonly GeminiLiveSessionService _liveSessionService;
    private readonly OverlayService _overlayService;
    private readonly SpeechPlaybackService _speechPlaybackService;
    private CancellationTokenSource? _screenLoopCts;
    private bool _isRunning;
    private string _lastStatusMessage = string.Empty;

    public GameAssistantOrchestrator(
        Func<AppSettings> settingsProvider,
        ScreenCaptureService screenCaptureService,
        AudioRecordingService audioRecordingService,
        GeminiLiveSessionService liveSessionService,
        OverlayService overlayService,
        SpeechPlaybackService speechPlaybackService)
    {
        _settingsProvider = settingsProvider;
        _screenCaptureService = screenCaptureService;
        _audioRecordingService = audioRecordingService;
        _liveSessionService = liveSessionService;
        _overlayService = overlayService;
        _speechPlaybackService = speechPlaybackService;

        _audioRecordingService.AudioChunkAvailable += OnAudioChunkAvailable;
        _liveSessionService.StatusChanged += message =>
        {
            _lastStatusMessage = message;
            StatusChanged?.Invoke(message);
        };
        _liveSessionService.SessionStateChanged += isRunning =>
        {
            var wasRunning = _isRunning;
            _isRunning = isRunning;
            SessionStateChanged?.Invoke(isRunning);

            if (isRunning && !wasRunning)
            {
                var resumed = _lastStatusMessage.Contains("resumed", StringComparison.OrdinalIgnoreCase) ||
                              _lastStatusMessage.Contains("reconnected", StringComparison.OrdinalIgnoreCase);
                var message = resumed
                    ? "Live coaching resumed."
                    : "Live coaching started.";
                _ = _overlayService.ShowMessageAsync(message, TimeSpan.FromSeconds(3));
            }
            else if (!isRunning && wasRunning)
            {
                _ = _overlayService.ShowMessageAsync("Live coaching stopped.", TimeSpan.FromSeconds(3));
            }
        };
        _liveSessionService.InputTranscriptionChanged += text => InputTranscriptionChanged?.Invoke(text);
        _liveSessionService.OutputTranscriptionChanged += text =>
        {
            OutputTranscriptionChanged?.Invoke(text);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _ = _overlayService.ShowMessageAsync(
                    text,
                    TimeSpan.FromSeconds(_settingsProvider().OverlayDurationSeconds));
            }
        };
        _liveSessionService.AudioReceived += bytes => _speechPlaybackService.EnqueuePcm(bytes);
        _liveSessionService.Interrupted += () => _speechPlaybackService.Clear();
    }

    public event Action<string>? StatusChanged;

    public event Action<bool>? SessionStateChanged;

    public event Action<string>? InputTranscriptionChanged;

    public event Action<string>? OutputTranscriptionChanged;

    public async Task StartLiveSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            StatusChanged?.Invoke("Live session is already running.");
            return;
        }

        var settings = _settingsProvider();
        await _liveSessionService.StartAsync(settings, cancellationToken);

        _audioRecordingService.StartStreaming(settings.MicrophoneDeviceName, settings.MicrophoneGain);
        StartScreenLoop(settings);

        StatusChanged?.Invoke("Live session started.");
    }

    public async Task StopLiveSessionAsync(CancellationToken cancellationToken = default)
    {
        StopScreenLoop();
        _audioRecordingService.StopStreaming();
        _speechPlaybackService.Clear();
        await _liveSessionService.StopAsync(cancellationToken);
        await _overlayService.HideAsync();
        StatusChanged?.Invoke("Live session stopped.");
    }

    private void StartScreenLoop(AppSettings settings)
    {
        StopScreenLoop();

        if (!settings.StreamScreenFrames)
        {
            return;
        }

        _screenLoopCts = new CancellationTokenSource();
        _ = StreamScreenFramesAsync(settings, _screenLoopCts.Token);
    }

    private void StopScreenLoop()
    {
        _screenLoopCts?.Cancel();
        _screenLoopCts?.Dispose();
        _screenLoopCts = null;
    }

    private async Task StreamScreenFramesAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var frame = _screenCaptureService.CaptureMonitorJpeg(settings.ScreenDeviceName, settings.ScreenshotJpegQuality);
                await _liveSessionService.SendVideoFrameAsync(frame, cancellationToken);
                await Task.Delay(settings.ScreenCaptureIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Screen streaming error: {ex.Message}");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private void OnAudioChunkAvailable(AudioChunk chunk)
    {
        if (!_isRunning)
        {
            return;
        }

        _ = _liveSessionService.SendAudioChunkAsync(chunk);
    }

    public async ValueTask DisposeAsync()
    {
        _audioRecordingService.AudioChunkAvailable -= OnAudioChunkAvailable;
        await StopLiveSessionAsync();
    }

    public void UpdateMicrophoneGain(double gain)
    {
        _audioRecordingService.InputGain = gain;
    }
}
