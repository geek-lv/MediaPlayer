namespace MediaPlayer.Controls.Audio;

public interface IMediaAudioPlaybackController
{
    bool SupportsVolumeControl { get; }

    bool SupportsMuteControl { get; }

    float Volume { get; }

    bool IsMuted { get; }
}
