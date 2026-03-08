namespace WinAiBuddy.Models;

public sealed class ConversationLogEntryRecord
{
    public DateTime Timestamp { get; set; }

    public string Role { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}
