using System.IO;
using NAudio.Wave;
using WinAiBuddy.Models;

namespace WinAiBuddy.Services;

public sealed class AudioRecordingService : IDisposable
{
    private WaveInEvent? _recorder;

    public event Action<AudioChunk>? AudioChunkAvailable;

    public void StartStreaming()
    {
        StopStreaming();

        if (WaveIn.DeviceCount == 0)
        {
            throw new InvalidOperationException("No microphone input device was found.");
        }

        _recorder = new WaveInEvent
        {
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
