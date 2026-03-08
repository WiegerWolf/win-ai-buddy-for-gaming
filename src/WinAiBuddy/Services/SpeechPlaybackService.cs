using NAudio.Wave;

namespace WinAiBuddy.Services;

public sealed class SpeechPlaybackService : IDisposable
{
    private readonly object _sync = new();
    private readonly WaveOutEvent _outputDevice;
    private readonly BufferedWaveProvider _buffer;

    public SpeechPlaybackService()
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(15),
            DiscardOnBufferOverflow = true
        };
        _outputDevice = new WaveOutEvent();
        _outputDevice.Init(_buffer);
        _outputDevice.Play();
    }

    public void EnqueuePcm(byte[] bytes)
    {
        lock (_sync)
        {
            _buffer.AddSamples(bytes, 0, bytes.Length);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _buffer.ClearBuffer();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _outputDevice.Stop();
            _outputDevice.Dispose();
        }
    }
}
