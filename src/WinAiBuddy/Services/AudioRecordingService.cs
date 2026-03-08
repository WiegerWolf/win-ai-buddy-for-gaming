using System.IO;
using NAudio.Wave;
using WinAiBuddy.Models;

namespace WinAiBuddy.Services;

public sealed class AudioRecordingService : IDisposable
{
    private WaveInEvent? _recorder;

    public event Action<AudioChunk>? AudioChunkAvailable;

    public static IReadOnlyList<AudioInputDeviceOption> GetInputDevices()
    {
        var devices = new List<AudioInputDeviceOption>();

        for (var index = 0; index < WaveIn.DeviceCount; index++)
        {
            var capabilities = WaveIn.GetCapabilities(index);
            devices.Add(new AudioInputDeviceOption(
                index,
                capabilities.ProductName,
                $"{capabilities.ProductName} (Input {index + 1})"));
        }

        return devices;
    }

    public void StartStreaming(string? preferredDeviceName = null)
    {
        StopStreaming();

        var devices = GetInputDevices();
        if (devices.Count == 0)
        {
            throw new InvalidOperationException("No microphone input device was found.");
        }

        var selectedDevice = devices.FirstOrDefault(device =>
            string.Equals(device.DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? devices[0];

        _recorder = new WaveInEvent
        {
            DeviceNumber = selectedDevice.DeviceNumber,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100
        };

        _recorder.DataAvailable += (_, args) =>
        {
            var bytes = new byte[args.BytesRecorded];
            Buffer.BlockCopy(args.Buffer, 0, bytes, 0, args.BytesRecorded);
            AudioChunkAvailable?.Invoke(new AudioChunk(bytes, "audio/pcm;rate=16000"));
        };

        _recorder.StartRecording();
    }

    public void StopStreaming()
    {
        if (_recorder is null)
        {
            return;
        }

        try
        {
            _recorder.StopRecording();
        }
        catch
        {
        }

        _recorder.Dispose();
        _recorder = null;
    }

    public void Dispose()
    {
        StopStreaming();
    }
}
