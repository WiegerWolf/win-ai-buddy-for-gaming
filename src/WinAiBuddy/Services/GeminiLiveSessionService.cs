using Google.GenAI;
using Google.GenAI.Types;
using WinAiBuddy.Models;

namespace WinAiBuddy.Services;

public sealed class GeminiLiveSessionService : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Client? _client;
    private AsyncSession? _session;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoopTask;

    public event Action<string>? StatusChanged;

    public event Action<bool>? SessionStateChanged;

    public event Action<string>? InputTranscriptionChanged;

    public event Action<string>? OutputTranscriptionChanged;

    public event Action<byte[]>? AudioReceived;

    public event Action? Interrupted;

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("A Gemini API key is required.");
        }

        _client = new Client(
            apiKey: settings.ApiKey,
            httpOptions: new HttpOptions
            {
                ApiVersion = "v1beta"
            });

        var config = new LiveConnectConfig
        {
            ResponseModalities = new List<Modality> { Modality.Audio },
            MediaResolution = MediaResolution.MediaResolutionLow,
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
            InputTranscriptionChanged?.Invoke(inputText);
        }

        if (message.ServerContent?.OutputTranscription?.Text is { Length: > 0 } outputText)
        {
            OutputTranscriptionChanged?.Invoke(outputText);
        }

        if (message.ServerContent?.Interrupted == true)
        {
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
            if (!string.IsNullOrWhiteSpace(part.Text))
            {
                OutputTranscriptionChanged?.Invoke(part.Text);
            }

            if (part.InlineData?.Data is { Length: > 0 } audioBytes)
            {
                AudioReceived?.Invoke(audioBytes);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sendLock.Dispose();
    }
}
