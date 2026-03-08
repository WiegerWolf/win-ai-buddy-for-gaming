namespace WinAiBuddy.Models;

public sealed class ConversationSessionRecord
{
    public string Id { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public DateTime LastUpdatedAt { get; set; }

    public string Status { get; set; } = "Active";

    public string Model { get; set; } = string.Empty;

    public List<ConversationLogEntryRecord> Entries { get; set; } = [];
}
