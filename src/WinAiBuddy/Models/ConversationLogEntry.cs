using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinAiBuddy.Models;

public sealed class ConversationLogEntry : INotifyPropertyChanged
{
    private DateTime _timestamp;
    private string _text;

    public ConversationLogEntry(DateTime timestamp, string role, string text)
    {
        _timestamp = timestamp;
        Role = role;
        _text = text;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DateTime Timestamp
    {
        get => _timestamp;
        set
        {
            if (_timestamp == value)
            {
                return;
            }

            _timestamp = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTimestamp));
        }
    }

    public string DisplayTimestamp => Timestamp.ToString("HH:mm:ss");

    public string Role { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (string.Equals(_text, value, StringComparison.Ordinal))
            {
                return;
            }

            _text = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
