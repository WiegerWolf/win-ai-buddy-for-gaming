namespace WinAiBuddy.Models;

public sealed class AppSettings
{
    public string AppTheme { get; set; } = "System";

    public string ApiKey { get; set; } = string.Empty;

    public string LiveModel { get; set; } = "gemini-2.5-flash-native-audio-preview-12-2025";

    public bool EnableAffectiveDialog { get; set; }

    public bool EnableProactiveAudio { get; set; }

    public bool EnableContextWindowCompression { get; set; }

    public int ContextCompressionTriggerTokens { get; set; } = 24000;

    public int ContextCompressionTargetTokens { get; set; } = 12000;

    public string MediaResolution { get; set; } = "Low";

    public bool EnableThinkingConfig { get; set; }

    public string ThinkingMode { get; set; } = "Default";

    public int ThinkingBudget { get; set; } = -1;

    public string ThinkingLevel { get; set; } = "Default";

    public bool IncludeThoughts { get; set; }

    public string Voice { get; set; } = "Kore";

    public string MicrophoneDeviceName { get; set; } = string.Empty;

    public double MicrophoneGain { get; set; } = 1.0;

    public string ScreenDeviceName { get; set; } = string.Empty;

    public int ScreenCaptureIntervalMs { get; set; } = 1000;

    public int OverlayDurationSeconds { get; set; } = 10;

    public double OverlayOpacity { get; set; } = 0.94;

    public double OverlayFontSize { get; set; } = 26.0;

    public string OverlayBackgroundColor { get; set; } = "#111111";

    public double OverlayBackgroundOpacity { get; set; } = 0.85;

    public string OverlayTextColor { get; set; } = "#FFFFFFFF";

    public string OverlayOutlineColor { get; set; } = "#FF06131A";

    public double OverlayOutlineThickness { get; set; } = 2.5;

    public double? OverlayLeft { get; set; }

    public double? OverlayTop { get; set; }

    public int ScreenshotJpegQuality { get; set; } = 85;

    public bool StartMinimizedToTray { get; set; }

    public bool AutoStartSession { get; set; }

    public bool StreamScreenFrames { get; set; } = true;

    public string SystemPrompt { get; set; } =
        "You are a live gaming coach watching the player's live game and microphone input. " +
        "Speak naturally, keep advice short and immediately actionable, and avoid talking unless the player is asking for help or the situation clearly needs a warning.";
}
