using System.Text.RegularExpressions;
using Google.GenAI;
using Google.GenAI.Types;
using WinAiBuddy.Models;

namespace WinAiBuddy.Services;

public sealed class GeminiLiveSessionService : IAsyncDisposable
{
    private const int MaxBufferedRealtimeInputs = 1000;
    private const string TransparentUnsupportedMessage = "transparent parameter is not supported";
    private static readonly Regex DuplicateWhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex PunctuationSpacingRegex = new(@"(?<=[,!?;:.])(?=\p{L})", RegexOptions.Compiled);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly object _transcriptLock = new();
    private readonly object _bufferLock = new();
    private readonly DiagnosticsLogService _diagnosticsLogService;

    private Client? _client;
    private AsyncSession? _session;
    private CancellationTokenSource? _receiveLoopCts;
    private CancellationTokenSource? _serviceLifetimeCts;
    private Task? _receiveLoopTask;

    private AppSettings? _activeSettings;
    private string? _sessionResumptionHandle;
    private long _nextClientMessageIndex;
    private long _lastConsumedClientMessageIndex = -1;
    private bool _shouldBeRunning;
    private bool _transparentResumptionEnabled = true;

    private readonly List<BufferedRealtimeInput> _pendingRealtimeInputs = new();

    private string _currentInputTranscript = string.Empty;
    private string _currentOutputTranscript = string.Empty;
    private DateTime _lastInputTranscriptAt = DateTime.MinValue;
    private DateTime _lastOutputTranscriptAt = DateTime.MinValue;
    private DateTime _lastBufferedInputLogAt = DateTime.MinValue;
    private DateTime _lastReceivedAudioLogAt = DateTime.MinValue;
    private bool _inputTurnOpen;
    private bool _outputTurnOpen;
    private int _bufferedAudioChunksSinceLastLog;
    private int _bufferedVideoFramesSinceLastLog;
    private int _receivedAudioChunksSinceLastLog;

    public event Action<string>? StatusChanged;

    public event Action<bool>? SessionStateChanged;

    public event Action<string>? InputTranscriptionChanged;

    public event Action<string>? OutputTranscriptionChanged;

    public event Action<byte[]>? AudioReceived;

    public event Action? Interrupted;

    public GeminiLiveSessionService(DiagnosticsLogService diagnosticsLogService)
    {
        _diagnosticsLogService = diagnosticsLogService;
    }

    public async Task StartAsync(
        AppSettings settings,
        IReadOnlyList<ConversationLogEntryRecord>? restoredConversation = null,
        CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        ResetTranscriptState();
        ClearRecoveryState();

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("A Gemini API key is required.");
        }

        _activeSettings = settings;
        _shouldBeRunning = true;
        _serviceLifetimeCts = new CancellationTokenSource();
        ResetDiagnosticsCounters();
        var sessionLogPath = _diagnosticsLogService.StartSession(settings.LiveModel);
        LogSession("Live", $"Start requested | model={settings.LiveModel} | voice={settings.Voice} | streamScreen={settings.StreamScreenFrames} | restoredTurns={restoredConversation?.Count ?? 0} | log={sessionLogPath}");

        await ConnectAsync(settings, allowResumption: false, cancellationToken);

        if (restoredConversation is { Count: > 0 } restoredTurns)
        {
            await RestoreConversationContextAsync(restoredTurns, cancellationToken);
        }

        SessionStateChanged?.Invoke(true);
        PublishStatus("Connected to Gemini Live.");
    }

    public async Task SendAudioChunkAsync(AudioChunk chunk, CancellationToken cancellationToken = default)
    {
        if (!_shouldBeRunning)
        {
            return;
        }

        var buffered = BufferAudio(chunk);
        var session = _session;
        if (session is null)
        {
            _ = RecoverSessionAsync("Audio input arrived while the live session was reconnecting.");
            return;
        }

        await SendBufferedRealtimeInputAsync(session, buffered, cancellationToken);
    }

    public async Task SendVideoFrameAsync(ScreenshotCapture frame, CancellationToken cancellationToken = default)
    {
        if (!_shouldBeRunning)
        {
            return;
        }

        var buffered = BufferVideo(frame);
        var session = _session;
        if (session is null)
        {
            _ = RecoverSessionAsync("Screen input arrived while the live session was reconnecting.");
            return;
        }

        await SendBufferedRealtimeInputAsync(session, buffered, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _shouldBeRunning = false;

        _serviceLifetimeCts?.Cancel();

        await DisposeCurrentSessionAsync(resetTranscripts: true, cancellationToken);

        _serviceLifetimeCts?.Dispose();
        _serviceLifetimeCts = null;
        _activeSettings = null;

        ClearRecoveryState();
        LogSession("Live", "Stop completed.");
        _diagnosticsLogService.EndSession("Stopped");
        SessionStateChanged?.Invoke(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sendLock.Dispose();
        _reconnectLock.Dispose();
    }

    private async Task ConnectAsync(AppSettings settings, bool allowResumption, CancellationToken cancellationToken)
    {
        LogSession("Connect", $"Connecting | allowResumption={allowResumption} | apiVersion={(RequiresV1Alpha(settings) ? "v1alpha" : "v1beta")} | transparent={_transparentResumptionEnabled} | handlePresent={!string.IsNullOrWhiteSpace(_sessionResumptionHandle)}");
        _client?.Dispose();
        _client = new Client(
            apiKey: settings.ApiKey,
            httpOptions: new HttpOptions
            {
                ApiVersion = RequiresV1Alpha(settings) ? "v1alpha" : "v1beta"
            });

        var config = BuildConnectConfig(settings, allowResumption);

        try
        {
            _session = await _client.Live.ConnectAsync(settings.LiveModel, config, cancellationToken);
        }
        catch (Exception ex) when (_transparentResumptionEnabled && SupportsTransparentFallback(ex))
        {
            _transparentResumptionEnabled = false;
            LogSession("Connect", $"Transparent resumption rejected by Gemini. Falling back. | {ex.Message}");
            PublishStatus("Gemini Live does not support transparent session resumption here. Falling back to handle-based recovery.");

            config = BuildConnectConfig(settings, allowResumption);
            _session = await _client.Live.ConnectAsync(settings.LiveModel, config, cancellationToken);
        }

        LogSession("Connect", "Connected.");

        _receiveLoopCts?.Dispose();
        _receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(
            _serviceLifetimeCts?.Token ?? CancellationToken.None);
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_receiveLoopCts.Token));
    }

    private LiveConnectConfig BuildConnectConfig(AppSettings settings, bool allowResumption)
    {
        return new LiveConnectConfig
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
            SessionResumption = new SessionResumptionConfig
            {
                Handle = allowResumption ? _sessionResumptionHandle : null,
                Transparent = _transparentResumptionEnabled ? true : null
            },
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
            Tools = new List<Tool>
            {
                new()
                {
                    GoogleSearch = new GoogleSearch()
                }
            },
            InputAudioTranscription = new AudioTranscriptionConfig(),
            OutputAudioTranscription = new AudioTranscriptionConfig()
        };
    }

    private async Task SendBufferedRealtimeInputAsync(
        AsyncSession session,
        BufferedRealtimeInput buffered,
        CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(session, _session))
            {
                return;
            }

            await session.SendRealtimeInputAsync(buffered.ToRealtimeInput(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Live session send interrupted: {ex.Message}. Attempting recovery...");
            _ = RecoverSessionAsync($"Realtime send failed: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task RestoreConversationContextAsync(
        IReadOnlyList<ConversationLogEntryRecord> restoredConversation,
        CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        var turns = BuildRestoredConversationTurns(restoredConversation);
        if (turns.Count == 0)
        {
            LogSession("Restore", "Resume requested, but no valid turns were produced from saved conversation.");
            return;
        }

        LogSession("Restore", $"Loading saved context | turns={turns.Count}");
        await session.SendClientContentAsync(new LiveSendClientContentParameters
        {
            Turns = turns,
            TurnComplete = false
        }, cancellationToken);

        PublishStatus("Loaded saved conversation context into Gemini Live.");
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        LogSession("Receive", "Receive loop started.");
        try
        {
            while (!cancellationToken.IsCancellationRequested && _session is not null)
            {
                var message = await _session.ReceiveAsync(cancellationToken);
                if (message is null)
                {
                    if (_shouldBeRunning)
                    {
                        LogSession("Receive", "ReceiveAsync returned null while session should still be running.");
                        _ = RecoverSessionAsync("Gemini Live closed the websocket. Reconnecting...");
                    }

                    break;
                }

                ProcessMessage(message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogSession("Receive", "Receive loop cancelled.");
        }
        catch (Exception ex)
        {
            if (_shouldBeRunning)
            {
                LogSessionException("Receive", "Receive loop failed.", ex);
                PublishStatus($"Live session error: {ex.Message}. Attempting recovery...");
                _ = RecoverSessionAsync($"Receive loop failed: {ex.Message}");
            }
        }
        finally
        {
            LogSession("Receive", "Receive loop ended.");
        }
    }

    private void ProcessMessage(LiveServerMessage message)
    {
        if (message.SessionResumptionUpdate is { } resumptionUpdate)
        {
            UpdateSessionResumptionState(resumptionUpdate);
        }

        LogServerMessage(message);

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
            LogSession("Server", "Generation interrupted.");
            ResetOutputTranscript();
            Interrupted?.Invoke();
        }

        if (message.GoAway is not null)
        {
            PublishStatus("Gemini Live asked this session to reconnect soon. Resuming automatically...");
            _ = RecoverSessionAsync("Gemini Live sent GoAway.");
        }

        var parts = message.ServerContent?.ModelTurn?.Parts;
        if (parts is not null)
        {
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
                    LogReceivedAudioSummary(audioBytes.Length);
                    AudioReceived?.Invoke(audioBytes);
                }
            }
        }

        if (message.ServerContent?.GenerationComplete == true || message.ServerContent?.TurnComplete == true)
        {
            CloseTranscriptTurns();
        }
    }

    private async Task RecoverSessionAsync(string reason)
    {
        if (!_shouldBeRunning || _activeSettings is null || _serviceLifetimeCts?.IsCancellationRequested == true)
        {
            return;
        }

        await _reconnectLock.WaitAsync();
        try
        {
            if (!_shouldBeRunning || _activeSettings is null || _serviceLifetimeCts?.IsCancellationRequested == true)
            {
                return;
            }

            var attempt = 0;
            while (_shouldBeRunning && _activeSettings is not null && _serviceLifetimeCts?.IsCancellationRequested != true)
            {
                attempt++;
                try
                {
                    PublishStatus(attempt == 1
                        ? $"Recovering live session: {reason}"
                        : $"Reconnect attempt {attempt} after: {reason}");
                    LogSession("Recover", $"Reconnect attempt {attempt} started | reason={reason}");

                    await DisposeCurrentSessionAsync(resetTranscripts: false, CancellationToken.None);
                    await ConnectAsync(_activeSettings, allowResumption: true, CancellationToken.None);
                    if (_transparentResumptionEnabled)
                    {
                        await ReplayBufferedRealtimeInputsAsync(CancellationToken.None);
                    }
                    else
                    {
                        TrimBufferedInputsAfterOpaqueReconnect();
                    }

                    PublishStatus(string.IsNullOrWhiteSpace(_sessionResumptionHandle)
                        ? "Live session reconnected."
                        : _transparentResumptionEnabled
                            ? "Live session reconnected and resumed."
                            : "Live session reconnected and resumed context.");
                    LogSession("Recover", $"Reconnect attempt {attempt} succeeded | handlePresent={!string.IsNullOrWhiteSpace(_sessionResumptionHandle)} | transparent={_transparentResumptionEnabled}");
                    return;
                }
                catch (Exception ex)
                {
                    LogSessionException("Recover", $"Reconnect attempt {attempt} failed.", ex);
                    PublishStatus($"Reconnect attempt {attempt} failed: {ex.Message}. Retrying...");
                    var delayMs = Math.Min(5000, 750 * attempt);
                    await Task.Delay(delayMs, _serviceLifetimeCts?.Token ?? CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private async Task ReplayBufferedRealtimeInputsAsync(CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        List<BufferedRealtimeInput> replayItems;
        lock (_bufferLock)
        {
            replayItems = _pendingRealtimeInputs
                .Where(item => item.Index > _lastConsumedClientMessageIndex)
                .OrderBy(item => item.Index)
                .ToList();
        }

        if (replayItems.Count == 0)
        {
            LogSession("Recover", "No buffered realtime inputs needed replay.");
            return;
        }

        LogSession("Recover", $"Replaying buffered realtime inputs | count={replayItems.Count} | lastConsumed={_lastConsumedClientMessageIndex}");

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var item in replayItems)
            {
                if (!ReferenceEquals(session, _session))
                {
                    return;
                }

                await session.SendRealtimeInputAsync(item.ToRealtimeInput(), cancellationToken);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task DisposeCurrentSessionAsync(bool resetTranscripts, CancellationToken cancellationToken)
    {
        _receiveLoopCts?.Cancel();

        if (_session is not null)
        {
            try
            {
                await _session.CloseAsync();
            }
            catch
            {
                LogSession("Dispose", "Session close raised and was ignored.");
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

        if (resetTranscripts)
        {
            ResetTranscriptState();
        }

        LogSession("Dispose", $"Disposed current session | resetTranscripts={resetTranscripts}");
    }

    private void UpdateSessionResumptionState(LiveServerSessionResumptionUpdate update)
    {
        lock (_bufferLock)
        {
            if (update.Resumable == true && !string.IsNullOrWhiteSpace(update.NewHandle))
            {
                _sessionResumptionHandle = update.NewHandle;
            }

            if (update.LastConsumedClientMessageIndex is { } consumedIndex)
            {
                _lastConsumedClientMessageIndex = Convert.ToInt64(consumedIndex);
                _pendingRealtimeInputs.RemoveAll(item => item.Index <= _lastConsumedClientMessageIndex);
            }
        }

        LogSession("Resumption", $"Update | resumable={update.Resumable} | handlePresent={!string.IsNullOrWhiteSpace(update.NewHandle)} | lastConsumed={update.LastConsumedClientMessageIndex?.ToString() ?? "n/a"}");
    }

    private BufferedRealtimeInput BufferAudio(AudioChunk chunk)
    {
        var blob = new Blob
        {
            Data = chunk.Bytes.ToArray(),
            MimeType = chunk.MimeType
        };

        return BufferRealtimeInput(audio: blob, video: null);
    }

    private BufferedRealtimeInput BufferVideo(ScreenshotCapture frame)
    {
        var blob = new Blob
        {
            Data = frame.Bytes.ToArray(),
            MimeType = frame.MimeType
        };

        return BufferRealtimeInput(audio: null, video: blob);
    }

    private BufferedRealtimeInput BufferRealtimeInput(Blob? audio, Blob? video)
    {
        lock (_bufferLock)
        {
            var buffered = new BufferedRealtimeInput(++_nextClientMessageIndex, audio, video);
            _pendingRealtimeInputs.Add(buffered);

            if (_pendingRealtimeInputs.Count > MaxBufferedRealtimeInputs)
            {
                _pendingRealtimeInputs.RemoveAt(0);
            }

            UpdateBufferedInputLogState(audio is not null, video is not null, buffered.Index, _pendingRealtimeInputs.Count);

            return buffered;
        }
    }

    private void ClearRecoveryState()
    {
        lock (_bufferLock)
        {
            _pendingRealtimeInputs.Clear();
            _sessionResumptionHandle = null;
            _nextClientMessageIndex = 0;
            _lastConsumedClientMessageIndex = -1;
        }

        _transparentResumptionEnabled = true;
        LogSession("Recover", "Recovery state cleared.");
    }

    private void TrimBufferedInputsAfterOpaqueReconnect()
    {
        lock (_bufferLock)
        {
            _pendingRealtimeInputs.Clear();
            _lastConsumedClientMessageIndex = _nextClientMessageIndex;
        }

        LogSession("Recover", "Transparent replay unavailable. Cleared buffered realtime inputs after reconnect.");
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
            InputTranscriptionChanged?.Invoke(NormalizeTranscriptForDisplay(aggregated));
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

            OutputTranscriptionChanged?.Invoke(NormalizeTranscriptForDisplay(aggregated));
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

    private static List<Content> BuildRestoredConversationTurns(IReadOnlyList<ConversationLogEntryRecord> restoredConversation)
    {
        var turns = new List<Content>();
        Content? current = null;
        string? currentRole = null;

        foreach (var entry in restoredConversation)
        {
            var text = entry.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var role = MapSavedConversationRole(entry.Role);
            if (role is null)
            {
                continue;
            }

            if (!string.Equals(currentRole, role, StringComparison.Ordinal))
            {
                current = new Content
                {
                    Role = role,
                    Parts = new List<Part>()
                };
                turns.Add(current);
                currentRole = role;
            }

            current!.Parts ??= new List<Part>();
            current.Parts.Add(new Part { Text = text });
        }

        return turns;
    }

    private static string? MapSavedConversationRole(string? role)
    {
        if (string.Equals(role, "You", StringComparison.OrdinalIgnoreCase))
        {
            return "user";
        }

        if (string.Equals(role, "Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return "model";
        }

        return null;
    }

    private static string NormalizeTranscriptForDisplay(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return transcript;
        }

        var normalized = PunctuationSpacingRegex.Replace(transcript, " ");
        normalized = DuplicateWhitespaceRegex.Replace(normalized, " ");
        return normalized.Trim();
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

    private static bool SupportsTransparentFallback(Exception ex)
    {
        return ex.Message.Contains(TransparentUnsupportedMessage, StringComparison.OrdinalIgnoreCase);
    }

    private void PublishStatus(string message)
    {
        LogSession("Status", message);
        StatusChanged?.Invoke(message);
    }

    private void LogServerMessage(LiveServerMessage message)
    {
        var modelParts = message.ServerContent?.ModelTurn?.Parts;
        var textParts = modelParts?.Count(part => !string.IsNullOrWhiteSpace(part.Text) && part.Thought != true) ?? 0;
        var thoughtParts = modelParts?.Count(part => part.Thought == true) ?? 0;
        var audioParts = modelParts?.Count(part => part.InlineData?.Data is { Length: > 0 }) ?? 0;

        LogSession(
            "Server",
            $"Message | goAway={message.GoAway is not null} | interrupted={message.ServerContent?.Interrupted == true} | turnComplete={message.ServerContent?.TurnComplete == true} | generationComplete={message.ServerContent?.GenerationComplete == true} | inputText={(message.ServerContent?.InputTranscription?.Text?.Length ?? 0)} chars | outputText={(message.ServerContent?.OutputTranscription?.Text?.Length ?? 0)} chars | textParts={textParts} | thoughtParts={thoughtParts} | audioParts={audioParts}");
    }

    private void ResetDiagnosticsCounters()
    {
        _lastBufferedInputLogAt = DateTime.MinValue;
        _lastReceivedAudioLogAt = DateTime.MinValue;
        _bufferedAudioChunksSinceLastLog = 0;
        _bufferedVideoFramesSinceLastLog = 0;
        _receivedAudioChunksSinceLastLog = 0;
    }

    private void UpdateBufferedInputLogState(bool hasAudio, bool hasVideo, long index, int pendingCount)
    {
        var now = DateTime.UtcNow;

        if (hasAudio)
        {
            _bufferedAudioChunksSinceLastLog++;
        }

        if (hasVideo)
        {
            _bufferedVideoFramesSinceLastLog++;
        }

        if ((now - _lastBufferedInputLogAt) < TimeSpan.FromSeconds(5))
        {
            return;
        }

        LogSession(
            "Buffer",
            $"Buffered realtime input summary | latestIndex={index} | pending={pendingCount} | audioChunks={_bufferedAudioChunksSinceLastLog} | videoFrames={_bufferedVideoFramesSinceLastLog}");
        _bufferedAudioChunksSinceLastLog = 0;
        _bufferedVideoFramesSinceLastLog = 0;
        _lastBufferedInputLogAt = now;
    }

    private void LogReceivedAudioSummary(int bytes)
    {
        var now = DateTime.UtcNow;
        _receivedAudioChunksSinceLastLog++;

        if ((now - _lastReceivedAudioLogAt) < TimeSpan.FromSeconds(5))
        {
            return;
        }

        LogSession(
            "Audio",
            $"Received output audio summary | chunks={_receivedAudioChunksSinceLastLog} | lastChunkBytes={bytes}");
        _receivedAudioChunksSinceLastLog = 0;
        _lastReceivedAudioLogAt = now;
    }

    private void LogSession(string category, string message)
    {
        _diagnosticsLogService.LogSession(category, message);
    }

    private void LogSessionException(string category, string message, Exception exception)
    {
        _diagnosticsLogService.LogSessionException(category, message, exception);
    }

    private sealed record BufferedRealtimeInput(long Index, Blob? Audio, Blob? Video)
    {
        public LiveSendRealtimeInputParameters ToRealtimeInput()
        {
            return new LiveSendRealtimeInputParameters
            {
                Audio = Audio is null ? null : new Blob
                {
                    Data = Audio.Data is null ? Array.Empty<byte>() : Audio.Data.ToArray(),
                    MimeType = Audio.MimeType
                },
                Video = Video is null ? null : new Blob
                {
                    Data = Video.Data is null ? Array.Empty<byte>() : Video.Data.ToArray(),
                    MimeType = Video.MimeType
                }
            };
        }
    }
}
