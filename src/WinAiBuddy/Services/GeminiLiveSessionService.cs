using Google.GenAI;
using Google.GenAI.Types;
using WinAiBuddy.Models;

namespace WinAiBuddy.Services;

public sealed class GeminiLiveSessionService : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _transcriptLock = new();
    private Client? _client;
    private AsyncSession? _session;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoopTask;
    private string _currentInputTranscript = string.Empty;
    private string _currentOutputTranscript = string.Empty;
    private DateTime _lastInputTranscriptAt = DateTime.MinValue;
    private DateTime _lastOutputTranscriptAt = DateTime.MinValue;
    private bool _inputTurnOpen;
    private bool _outputTurnOpen;

    public event Action<string>? StatusChanged;

    public event Action<bool>? SessionStateChanged;

    public event Action<string>? InputTranscriptionChanged;

    public event Action<string>? OutputTranscriptionChanged;

    public event Action<byte[]>? AudioReceived;

    public event Action? Interrupted;

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        ResetTranscriptState();

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("A Gemini API key is required.");
        }

        _client = new Client(
            apiKey: settings.ApiKey,
            httpOptions: new HttpOptions
            {
                ApiVersion = RequiresV1Alpha(settings) ? "v1alpha" : "v1beta"
            });

        var config = new LiveConnectConfig
        {
            ResponseModalities = new List<Modality> { Modality.Audio },
            EnableAffectiveDialog = settings.EnableAffectiveDialog,
            MediaResolution = ParseMediaResolution(settings.MediaResolution),
            Proactivity = settings.EnableProactiveAudio
                ? new ProactivityConfig
                {
                    ProactiveAudio = true
                }
                : null,
            ContextWindowCompression = BuildContextWindowCompression(settings),
            ThinkingConfig = BuildThinkingConfig(settings),
            SystemInstruction = new Content
            {
                Parts = new List<Part>
                {
                    new() { Text = settings.SystemPrompt }
                }
            },
            SpeechConfig = new SpeechConfig
            {
                VoiceConfig = new VoiceConfig
                {
                    PrebuiltVoiceConfig = new PrebuiltVoiceConfig
                    {
                        VoiceName = settings.Voice
                    }
                }
            },
            InputAudioTranscription = new AudioTranscriptionConfig(),
            OutputAudioTranscription = new AudioTranscriptionConfig()
        };

        _session = await _client.Live.ConnectAsync(settings.LiveModel, config, cancellationToken);
        _receiveLoopCts = new CancellationTokenSource();
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_receiveLoopCts.Token));

        SessionStateChanged?.Invoke(true);
        StatusChanged?.Invoke("Connected to Gemini Live.");
    }

    public async Task SendAudioChunkAsync(AudioChunk chunk, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new InvalidOperationException("Live session is not running.");
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await session.SendRealtimeInputAsync(
                new LiveSendRealtimeInputParameters
                {
                    Audio = new Blob
                    {
                        Data = chunk.Bytes,
                        MimeType = chunk.MimeType
                    }
                },
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendVideoFrameAsync(ScreenshotCapture frame, CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await session.SendRealtimeInputAsync(
                new LiveSendRealtimeInputParameters
                {
                    Video = new Blob
                    {
                        Data = frame.Bytes,
                        MimeType = frame.MimeType
                    }
                },
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _receiveLoopCts?.Cancel();
        ResetTranscriptState();

        if (_session is not null)
        {
            try
            {
                await _session.CloseAsync();
            }
            catch
            {
            }

            await _session.DisposeAsync();
            _session = null;
        }

        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask.WaitAsync(cancellationToken);
            }
            catch
            {
            }

            _receiveLoopTask = null;
        }

        _receiveLoopCts?.Dispose();
        _receiveLoopCts = null;

        _client?.Dispose();
        _client = null;

        SessionStateChanged?.Invoke(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _session is not null)
            {
                var message = await _session.ReceiveAsync(cancellationToken);
                if (message is null)
                {
                    break;
                }

                ProcessMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Live session error: {ex.Message}");
        }
        finally
        {
            SessionStateChanged?.Invoke(false);
        }
    }

    private void ProcessMessage(LiveServerMessage message)
    {
        if (message.ServerContent?.InputTranscription?.Text is { Length: > 0 } inputText)
        {
            PublishInputTranscript(inputText);
        }

        var emittedOutputTranscription = false;
        if (message.ServerContent?.OutputTranscription?.Text is { Length: > 0 } outputText)
        {
            PublishOutputTranscript(outputText);
            emittedOutputTranscription = true;
        }

        if (message.ServerContent?.Interrupted == true)
        {
            ResetOutputTranscript();
            Interrupted?.Invoke();
        }

        if (message.GoAway is not null)
        {
            StatusChanged?.Invoke("Gemini Live signaled that this session is nearing its limit.");
        }

        var parts = message.ServerContent?.ModelTurn?.Parts;
        if (parts is null)
        {
            return;
        }

        foreach (var part in parts)
        {
            if (!emittedOutputTranscription &&
                part.Thought != true &&
                !string.IsNullOrWhiteSpace(part.Text))
            {
                PublishOutputTranscript(part.Text);
            }

            if (part.InlineData?.Data is { Length: > 0 } audioBytes)
            {
                AudioReceived?.Invoke(audioBytes);
            }
        }

        if (message.ServerContent?.GenerationComplete == true || message.ServerContent?.TurnComplete == true)
        {
            CloseTranscriptTurns();
        }
    }

    private void PublishInputTranscript(string chunk)
    {
        var aggregated = MergeTranscriptChunk(
            chunk,
            ref _currentInputTranscript,
            ref _lastInputTranscriptAt,
            ref _inputTurnOpen);

        if (aggregated is not null)
        {
            InputTranscriptionChanged?.Invoke(aggregated);
        }
    }

    private void PublishOutputTranscript(string chunk)
    {
        var aggregated = MergeTranscriptChunk(
            chunk,
            ref _currentOutputTranscript,
            ref _lastOutputTranscriptAt,
            ref _outputTurnOpen);

        if (aggregated is not null)
        {
            lock (_transcriptLock)
            {
                _inputTurnOpen = false;
            }

            OutputTranscriptionChanged?.Invoke(aggregated);
        }
    }

    private string? MergeTranscriptChunk(
        string chunk,
        ref string currentTranscript,
        ref DateTime lastUpdatedAt,
        ref bool turnOpen)
    {
        var cleaned = chunk.Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        lock (_transcriptLock)
        {
            var now = DateTime.UtcNow;
            var shouldReset = !turnOpen || (now - lastUpdatedAt) > TimeSpan.FromSeconds(3);
            if (shouldReset)
            {
                currentTranscript = cleaned;
                turnOpen = true;
            }
            else
            {
                currentTranscript = MergeIncrementalText(currentTranscript, cleaned);
            }

            lastUpdatedAt = now;
            return currentTranscript;
        }
    }

    private static string MergeIncrementalText(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return incoming;
        }

        if (string.Equals(existing, incoming, StringComparison.Ordinal))
        {
            return existing;
        }

        if (incoming.StartsWith(existing, StringComparison.Ordinal))
        {
            return incoming;
        }

        if (existing.StartsWith(incoming, StringComparison.Ordinal))
        {
            return existing;
        }

        if (existing.EndsWith(incoming, StringComparison.Ordinal))
        {
            return existing;
        }

        if (incoming.Length > 0 &&
            char.IsLetterOrDigit(incoming[0]) &&
            existing.Length > 0 &&
            char.IsLetterOrDigit(existing[^1]))
        {
            var lastTokenLength = existing.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Length ?? 0;
            var firstTokenLength = incoming.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Length ?? 0;

            if (lastTokenLength <= 3 && firstTokenLength <= 4 && !incoming.Contains(' '))
            {
                return existing + incoming;
            }
        }

        if (incoming.Length > 0 && ".,!?;:)]}".Contains(incoming[0]))
        {
            return existing + incoming;
        }

        if (existing.Length > 0 && "([{".Contains(existing[^1]))
        {
            return existing + incoming;
        }

        return existing + " " + incoming;
    }

    private void CloseTranscriptTurns()
    {
        lock (_transcriptLock)
        {
            _inputTurnOpen = false;
            _outputTurnOpen = false;
        }
    }

    private void ResetTranscriptState()
    {
        lock (_transcriptLock)
        {
            _currentInputTranscript = string.Empty;
            _currentOutputTranscript = string.Empty;
            _lastInputTranscriptAt = DateTime.MinValue;
            _lastOutputTranscriptAt = DateTime.MinValue;
            _inputTurnOpen = false;
            _outputTurnOpen = false;
        }
    }

    private void ResetOutputTranscript()
    {
        lock (_transcriptLock)
        {
            _currentOutputTranscript = string.Empty;
            _lastOutputTranscriptAt = DateTime.MinValue;
            _outputTurnOpen = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sendLock.Dispose();
    }

    private static bool RequiresV1Alpha(AppSettings settings)
    {
        return settings.EnableAffectiveDialog || settings.EnableProactiveAudio;
    }

    private static ContextWindowCompressionConfig? BuildContextWindowCompression(AppSettings settings)
    {
        if (!settings.EnableContextWindowCompression)
        {
            return null;
        }

        var config = new ContextWindowCompressionConfig
        {
            TriggerTokens = Math.Max(1, settings.ContextCompressionTriggerTokens),
            SlidingWindow = new SlidingWindow()
        };

        if (settings.ContextCompressionTargetTokens > 0)
        {
            config.SlidingWindow.TargetTokens = settings.ContextCompressionTargetTokens;
        }

        return config;
    }

    private static ThinkingConfig? BuildThinkingConfig(AppSettings settings)
    {
        var mode = settings.ThinkingMode?.Trim().ToLowerInvariant();
        if (!settings.EnableThinkingConfig || mode == "default")
        {
            return null;
        }

        if (mode == "disabled")
        {
            return new ThinkingConfig
            {
                ThinkingBudget = 0
            };
        }

        return new ThinkingConfig
        {
            IncludeThoughts = settings.IncludeThoughts,
            ThinkingBudget = settings.ThinkingBudget,
            ThinkingLevel = ParseThinkingLevel(settings.ThinkingLevel)
        };
    }

    private static MediaResolution? ParseMediaResolution(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "default" or "" => (MediaResolution?)null,
            "low" => MediaResolution.MediaResolutionLow,
            "medium" => MediaResolution.MediaResolutionMedium,
            "high" => MediaResolution.MediaResolutionHigh,
            _ => MediaResolution.MediaResolutionLow
        };
    }

    private static ThinkingLevel? ParseThinkingLevel(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "default" or "" => (ThinkingLevel?)null,
            "minimal" => ThinkingLevel.Minimal,
            "low" => ThinkingLevel.Low,
            "medium" => ThinkingLevel.Medium,
            "high" => ThinkingLevel.High,
            _ => (ThinkingLevel?)null
        };
    }
}
