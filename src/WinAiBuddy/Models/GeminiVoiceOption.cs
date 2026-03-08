namespace WinAiBuddy.Models;

public sealed record GeminiVoiceOption(string Name, string Description)
{
    public string DisplayName => $"{Name} - {Description}";
}

