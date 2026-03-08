using System.IO;
using System.Media;

namespace WinAiBuddy.Services;

public sealed class VoiceSamplePlayer : IDisposable
{
    private readonly string _samplesDirectory;
    private SoundPlayer? _player;

    public VoiceSamplePlayer(string samplesDirectory)
    {
        _samplesDirectory = samplesDirectory;
    }

    public void Play(string voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            throw new InvalidOperationException("A voice must be selected before previewing.");
        }

        var path = Path.Combine(_samplesDirectory, $"{voiceName}.wav");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No local sample was found for voice '{voiceName}'.", path);
        }

        Stop();
        _player = new SoundPlayer(path);
        _player.Load();
        _player.Play();
    }

    public void Stop()
    {
        _player?.Stop();
        _player?.Dispose();
        _player = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
