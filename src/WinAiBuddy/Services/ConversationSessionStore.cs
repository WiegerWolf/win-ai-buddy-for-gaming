using System.IO;
using System.Text.Json;
using WinAiBuddy.Models;

namespace WinAiBuddy.Services;

public sealed class ConversationSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directoryPath;
    private readonly object _sync = new();

    public ConversationSessionStore(string directoryPath)
    {
        _directoryPath = directoryPath;
        Directory.CreateDirectory(_directoryPath);
    }

    public int RecoverInterruptedSessions()
    {
        lock (_sync)
        {
            var recovered = 0;

            foreach (var path in GetSessionPaths())
            {
                var record = ReadRecord(path);
                if (record is null)
                {
                    continue;
                }

                if (!string.Equals(record.Status, "Active", StringComparison.OrdinalIgnoreCase) &&
                    record.EndedAt is not null)
                {
                    continue;
                }

                record.Status = "Interrupted";
                record.EndedAt = record.LastUpdatedAt == default ? record.StartedAt : record.LastUpdatedAt;
                record.LastUpdatedAt = record.EndedAt.Value;
                WriteRecord(path, record);
                recovered++;
            }

            return recovered;
        }
    }

    public ConversationSessionRecord StartSession(
        string model,
        IEnumerable<ConversationLogEntryRecord>? seedEntries = null,
        string? resumedFromSessionId = null)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            var record = new ConversationSessionRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ResumedFromSessionId = resumedFromSessionId,
                StartedAt = now,
                LastUpdatedAt = now,
                Status = "Active",
                Model = model,
                Entries = seedEntries?
                    .Select(entry => new ConversationLogEntryRecord
                    {
                        Timestamp = entry.Timestamp,
                        Role = entry.Role,
                        Text = entry.Text
                    })
                    .ToList() ?? []
            };

            WriteRecord(GetPath(record.Id), record);
            return CloneRecord(record)!;
        }
    }

    public void SaveSnapshot(string sessionId, IEnumerable<ConversationLogEntry> entries)
    {
        lock (_sync)
        {
            var path = GetPath(sessionId);
            var record = ReadRecord(path);
            if (record is null)
            {
                return;
            }

            record.Entries = entries
                .Select(entry => new ConversationLogEntryRecord
                {
                    Timestamp = entry.Timestamp,
                    Role = entry.Role,
                    Text = entry.Text
                })
                .ToList();
            record.LastUpdatedAt = DateTime.UtcNow;

            if (record.EndedAt is null)
            {
                record.Status = "Active";
            }

            WriteRecord(path, record);
        }
    }

    public void EndSession(string sessionId, string status = "Stopped")
    {
        lock (_sync)
        {
            var path = GetPath(sessionId);
            var record = ReadRecord(path);
            if (record is null)
            {
                return;
            }

            var endedAt = DateTime.UtcNow;
            record.EndedAt = endedAt;
            record.LastUpdatedAt = endedAt;
            record.Status = string.IsNullOrWhiteSpace(status) ? "Stopped" : status;
            WriteRecord(path, record);
        }
    }

    public void ClearSessionEntries(string sessionId)
    {
        lock (_sync)
        {
            var path = GetPath(sessionId);
            var record = ReadRecord(path);
            if (record is null)
            {
                return;
            }

            record.Entries.Clear();
            record.LastUpdatedAt = DateTime.UtcNow;
            WriteRecord(path, record);
        }
    }

    public ConversationSessionRecord? GetSession(string sessionId)
    {
        lock (_sync)
        {
            return CloneRecord(ReadRecord(GetPath(sessionId)));
        }
    }

    public IReadOnlyList<ConversationSessionSummary> ListSessions()
    {
        lock (_sync)
        {
            return GetSessionPaths()
                .Select(ReadRecord)
                .Where(record => record is not null)
                .Select(record => new ConversationSessionSummary(
                    record!.Id,
                    record.ResumedFromSessionId,
                    record.StartedAt,
                    record.EndedAt,
                    record.LastUpdatedAt,
                    record.Status,
                    record.Entries.Count,
                    record.Model))
                .OrderByDescending(summary => summary.LastUpdatedAt == default ? summary.StartedAt : summary.LastUpdatedAt)
                .ThenByDescending(summary => summary.StartedAt)
                .ToList();
        }
    }

    private IEnumerable<string> GetSessionPaths()
    {
        return Directory.EnumerateFiles(_directoryPath, "*.json", SearchOption.TopDirectoryOnly);
    }

    private string GetPath(string sessionId)
    {
        return Path.Combine(_directoryPath, $"{sessionId}.json");
    }

    private static ConversationSessionRecord? ReadRecord(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ConversationSessionRecord>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteRecord(string path, ConversationSessionRecord record)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static ConversationSessionRecord? CloneRecord(ConversationSessionRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        return new ConversationSessionRecord
        {
            Id = record.Id,
            ResumedFromSessionId = record.ResumedFromSessionId,
            StartedAt = record.StartedAt,
            EndedAt = record.EndedAt,
            LastUpdatedAt = record.LastUpdatedAt,
            Status = record.Status,
            Model = record.Model,
            Entries = record.Entries
                .Select(entry => new ConversationLogEntryRecord
                {
                    Timestamp = entry.Timestamp,
                    Role = entry.Role,
                    Text = entry.Text
                })
                .ToList()
        };
    }
}
