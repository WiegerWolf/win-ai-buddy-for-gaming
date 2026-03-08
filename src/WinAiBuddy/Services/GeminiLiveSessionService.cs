using System.Text.RegularExpressions;
using Google.GenAI;
using Google.GenAI.Types;
using WinAiBuddy.Models;

namespace WinAiBuddy.Services;

public sealed class GeminiLiveSessionService : IAsyncDisposable
{
    private const int MaxBufferedRealtimeInputs = 1000;
    private const string TransparentUnsupportedMessage = "transparent parameter is not supported";
    private static readonly Regex PunctuationSpacingRegex = new(@"(?<=[,!?;:.])(?=\p{L})", RegexOptions.Compiled);
    private static readonly Regex GluedWordRegex = new(@"\p{L}{6,}", RegexOptions.Compiled);
    private static readonly HashSet<string> KnownTranscriptWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "ahead", "alert", "all", "allyour", "ambush", "an", "and", "another", "any", "approach",
        "area", "around", "at", "attack", "away", "back", "bad", "be", "before", "behind", "block", "bonfire",
        "bomb", "bombs", "boss", "bow", "break", "carefully", "can", "caution", "cautiously", "check", "circle",
        "clear", "close", "collectible", "compare", "continue", "counter", "cover", "current", "damage", "danger",
        "death", "deal", "decide", "definitely", "dodge", "don't", "down", "dragon", "earlier", "eight", "elite",
        "enemies", "enemy", "enough", "equip", "equipment", "estus", "exactly", "explore", "exploring", "fight",
        "find", "fire", "firebomb", "firebombs", "five", "flask", "flasks", "focus", "fog", "for", "found",
        "from", "gate", "get", "good", "got", "great", "guy", "guys", "have", "health", "heal", "help", "hidden",
        "humanity", "i", "if", "immediate", "in", "increase", "inventory", "is", "it", "item", "items", "jump",
        "jumping", "just", "keep", "kindle", "kindling", "later", "lead", "left", "less", "lets", "light",
        "likely", "look", "losing", "low", "main", "make", "manage", "mechanics", "menu", "merchant", "might",
        "more", "move", "need", "new", "next", "nice", "now", "npc", "number", "of", "off", "okay", "on", "one",
        "only", "or", "other", "out", "over", "parry", "path", "paths", "pick", "place", "plan", "platforms",
        "powder", "practical", "prepare", "proceed", "progress", "quick", "quickly", "ready", "really", "repair",
        "respawn", "rest", "return", "reverse", "right", "risk", "roll", "route", "safe", "safer", "save", "scale",
        "screen", "see", "select", "session", "set", "shield", "shortcut", "shortsword", "should", "side", "single",
        "situations", "slot", "slots", "slowly", "small", "so", "some", "souls", "space", "speak", "spots",
        "sword", "switch", "take", "target", "that", "the", "their", "them", "then", "there", "they", "thing",
        "this", "those", "threat", "through", "time", "timing", "to", "together", "trade", "trap", "try", "up",
        "use", "useful", "valuable", "very", "vigilant", "wall", "want", "warning", "was", "way", "we", "weapon",
        "weapons", "well", "what", "when", "where", "while", "white", "with", "work", "working", "you", "your"
    };

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly object _transcriptLock = new();
    private readonly object _bufferLock = new();

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
        ClearRecoveryState();

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("A Gemini API key is required.");
        }

        _activeSettings = settings;
        _shouldBeRunning = true;
        _serviceLifetimeCts = new CancellationTokenSource();

        await ConnectAsync(settings, allowResumption: false, cancellationToken);

        SessionStateChanged?.Invoke(true);
        StatusChanged?.Invoke("Connected to Gemini Live.");
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
            StatusChanged?.Invoke("Gemini Live does not support transparent session resumption here. Falling back to handle-based recovery.");

            config = BuildConnectConfig(settings, allowResumption);
            _session = await _client.Live.ConnectAsync(settings.LiveModel, config, cancellationToken);
        }

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

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _session is not null)
            {
                var message = await _session.ReceiveAsync(cancellationToken);
                if (message is null)
                {
                    if (_shouldBeRunning)
                    {
                        _ = RecoverSessionAsync("Gemini Live closed the websocket. Reconnecting...");
                    }

                    break;
                }

                ProcessMessage(message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_shouldBeRunning)
            {
                StatusChanged?.Invoke($"Live session error: {ex.Message}. Attempting recovery...");
                _ = RecoverSessionAsync($"Receive loop failed: {ex.Message}");
            }
        }
    }

    private void ProcessMessage(LiveServerMessage message)
    {
        if (message.SessionResumptionUpdate is { } resumptionUpdate)
        {
            UpdateSessionResumptionState(resumptionUpdate);
        }

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
            StatusChanged?.Invoke("Gemini Live asked this session to reconnect soon. Resuming automatically...");
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
                    StatusChanged?.Invoke(attempt == 1
                        ? $"Recovering live session: {reason}"
                        : $"Reconnect attempt {attempt} after: {reason}");

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

                    StatusChanged?.Invoke(string.IsNullOrWhiteSpace(_sessionResumptionHandle)
                        ? "Live session reconnected."
                        : _transparentResumptionEnabled
                            ? "Live session reconnected and resumed."
                            : "Live session reconnected and resumed context.");
                    return;
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"Reconnect attempt {attempt} failed: {ex.Message}. Retrying...");
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
            return;
        }

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
    }

    private void TrimBufferedInputsAfterOpaqueReconnect()
    {
        lock (_bufferLock)
        {
            _pendingRealtimeInputs.Clear();
            _lastConsumedClientMessageIndex = _nextClientMessageIndex;
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

    private static string NormalizeTranscriptForDisplay(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return transcript;
        }

        var normalized = PunctuationSpacingRegex.Replace(transcript, " ");
        normalized = GluedWordRegex.Replace(normalized, static match => SplitGluedWord(match.Value));
        return normalized;
    }

    private static string SplitGluedWord(string token)
    {
        var lowerToken = token.ToLowerInvariant();
        if (KnownTranscriptWords.Contains(lowerToken))
        {
            return token;
        }

        if (!TrySegmentToken(lowerToken, out var segmentLengths))
        {
            return token;
        }

        var segments = new List<string>(segmentLengths.Count);
        var index = 0;
        foreach (var length in segmentLengths)
        {
            segments.Add(token.Substring(index, length));
            index += length;
        }

        return string.Join(" ", segments);
    }

    private static bool TrySegmentToken(string token, out List<int> segmentLengths)
    {
        segmentLengths = [];
        if (token.Length < 6)
        {
            return false;
        }

        var best = new List<int>?[token.Length + 1];
        best[0] = [];

        for (var start = 0; start < token.Length; start++)
        {
            var current = best[start];
            if (current is null)
            {
                continue;
            }

            for (var length = 1; length <= Math.Min(12, token.Length - start); length++)
            {
                var piece = token.Substring(start, length);
                if (!KnownTranscriptWords.Contains(piece))
                {
                    continue;
                }

                var candidate = new List<int>(current) { length };
                var next = start + length;
                if (best[next] is null || IsBetterSegmentation(candidate, best[next]!))
                {
                    best[next] = candidate;
                }
            }
        }

        var result = best[token.Length];
        if (result is null || result.Count < 2)
        {
            return false;
        }

        var hasMeaningfulSplit = result.Any(length => length > 1);
        if (!hasMeaningfulSplit)
        {
            return false;
        }

        segmentLengths = result;
        return true;
    }

    private static bool IsBetterSegmentation(List<int> candidate, List<int> existing)
    {
        if (candidate.Count != existing.Count)
        {
            return candidate.Count > existing.Count;
        }

        var candidateShortest = candidate.Min();
        var existingShortest = existing.Min();
        if (candidateShortest != existingShortest)
        {
            return candidateShortest > existingShortest;
        }

        return candidate.Sum() > existing.Sum();
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
