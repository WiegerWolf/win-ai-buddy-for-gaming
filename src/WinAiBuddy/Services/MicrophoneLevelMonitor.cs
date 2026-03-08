using NAudio.Wave;
using WinAiBuddy.Models;

namespace WinAiBuddy.Services;

public sealed class MicrophoneLevelMonitor : IDisposable
{
    private WaveInEvent? _monitor;

    public event Action<float>? LevelChanged;

    public event Action<string>? StatusChanged;

    public void Start(string? preferredDeviceName)
    {
        Stop();

        var devices = AudioRecordingService.GetInputDevices();
        if (devices.Count == 0)
        {
            StatusChanged?.Invoke("No microphone detected.");
            LevelChanged?.Invoke(0);
            return;
        }

        var selectedDevice = devices.FirstOrDefault(device =>
            string.Equals(device.DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? devices[0];

        try
        {
            _monitor = new WaveInEvent
            {
                DeviceNumber = selectedDevice.DeviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 60
            };

            _monitor.DataAvailable += OnDataAvailable;
            _monitor.RecordingStopped += OnRecordingStopped;
            _monitor.StartRecording();
            StatusChanged?.Invoke($"Previewing {selectedDevice.DisplayName}.");
        }
        catch (Exception ex)
        {
            Stop();
            StatusChanged?.Invoke($"Mic preview unavailable: {ex.Message}");
            LevelChanged?.Invoke(0);
        }
    }

    public void Stop()
    {
        if (_monitor is null)
        {
            return;
        }

        try
        {
            _monitor.DataAvailable -= OnDataAvailable;
            _monitor.RecordingStopped -= OnRecordingStopped;
            _monitor.StopRecording();
        }
        catch
        {
        }

        _monitor.Dispose();
        _monitor = null;
        LevelChanged?.Invoke(0);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            LevelChanged?.Invoke(0);
            return;
        }

        short peak = 0;
        for (var index = 0; index < e.BytesRecorded; index += 2)
        {
            if (index + 1 >= e.BytesRecorded)
            {
                break;
            }

            var sample = BitConverter.ToInt16(e.Buffer, index);
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = (short)magnitude;
            }
        }

        var normalized = Math.Clamp(peak / (float)short.MaxValue, 0f, 1f);
        LevelChanged?.Invoke(normalized);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            StatusChanged?.Invoke($"Mic preview stopped: {e.Exception.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
