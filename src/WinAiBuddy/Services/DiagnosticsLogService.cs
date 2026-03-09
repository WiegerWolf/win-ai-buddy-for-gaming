using System.IO;

namespace WinAiBuddy.Services;

public sealed class DiagnosticsLogService
{
    private readonly object _sync = new();
    private readonly string _logsDirectoryPath;
    private readonly string _appLogPath;

    private string? _currentSessionLogPath;
    private string? _currentSessionId;

    public DiagnosticsLogService(string logsDirectoryPath)
    {
        _logsDirectoryPath = logsDirectoryPath;
        Directory.CreateDirectory(_logsDirectoryPath);
        _appLogPath = Path.Combine(_logsDirectoryPath, $"app-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    public string LogsDirectoryPath => _logsDirectoryPath;

    public string? CurrentSessionLogPath
    {
        get
        {
            lock (_sync)
            {
                return _currentSessionLogPath;
            }
        }
    }

    public string StartSession(string model)
    {
        lock (_sync)
        {
            _currentSessionId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
            _currentSessionLogPath = Path.Combine(_logsDirectoryPath, $"session-{_currentSessionId}.log");
            WriteLineUnsafe(_appLogPath, "Diagnostics", $"Session log started: {Path.GetFileName(_currentSessionLogPath)} | model={model}");
            WriteLineUnsafe(_currentSessionLogPath, "Diagnostics", $"Session log created | model={model}");
            return _currentSessionLogPath;
        }
    }

    public void EndSession(string reason)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_currentSessionLogPath))
            {
                return;
            }

            WriteLineUnsafe(_currentSessionLogPath, "Diagnostics", $"Session log closed | reason={reason}");
            WriteLineUnsafe(_appLogPath, "Diagnostics", $"Session log closed: {Path.GetFileName(_currentSessionLogPath)} | reason={reason}");
            _currentSessionLogPath = null;
            _currentSessionId = null;
        }
    }

    public void LogApp(string category, string message)
    {
        lock (_sync)
        {
            WriteLineUnsafe(_appLogPath, category, message);
        }
    }

    public void LogSession(string category, string message)
    {
        lock (_sync)
        {
            WriteLineUnsafe(_appLogPath, category, message);

            if (!string.IsNullOrWhiteSpace(_currentSessionLogPath))
            {
                WriteLineUnsafe(_currentSessionLogPath, category, message);
            }
        }
    }

    public void LogSessionException(string category, string message, Exception exception)
    {
        LogSession(category, $"{message}{Environment.NewLine}{exception}");
    }

    private static void WriteLineUnsafe(string path, string category, string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{category}] {message}{Environment.NewLine}";
        File.AppendAllText(path, line);
    }
}
