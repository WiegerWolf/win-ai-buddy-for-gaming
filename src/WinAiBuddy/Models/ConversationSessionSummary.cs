namespace WinAiBuddy.Models;

public sealed class ConversationSessionSummary
{
    public ConversationSessionSummary(string id, string? resumedFromSessionId, DateTime startedAt, DateTime? endedAt, DateTime lastUpdatedAt, string status, int entryCount, string model)
    {
        Id = id;
        ResumedFromSessionId = resumedFromSessionId;
        StartedAt = startedAt;
        EndedAt = endedAt;
        LastUpdatedAt = lastUpdatedAt;
        Status = string.IsNullOrWhiteSpace(status) ? "Saved" : status;
        EntryCount = entryCount;
        Model = model;
    }

    public string Id { get; }

    public string? ResumedFromSessionId { get; }

    public DateTime StartedAt { get; }

    public DateTime? EndedAt { get; }

    public DateTime LastUpdatedAt { get; }

    public string Status { get; }

    public int EntryCount { get; }

    public string Model { get; }

    public string DisplayTitle =>
        StartedAt.ToLocalTime().ToString("MMM d, HH:mm");

    public string DisplaySubtitle =>
        $"{(string.IsNullOrWhiteSpace(ResumedFromSessionId) ? Status : $"{Status} - Continued")} - {EntryCount} {(EntryCount == 1 ? "message" : "messages")}";

    public string DisplayDetail
    {
        get
        {
            var modelPart = string.IsNullOrWhiteSpace(Model) ? "Live session" : Model;
            var updatedAt = LastUpdatedAt == default ? StartedAt : LastUpdatedAt;
            return $"{modelPart} - Updated {updatedAt.ToLocalTime():HH:mm}";
        }
    }
}
