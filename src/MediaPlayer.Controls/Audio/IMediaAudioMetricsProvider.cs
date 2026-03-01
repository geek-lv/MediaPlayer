namespace MediaPlayer.Controls.Audio;

public interface IMediaAudioMetricsProvider
{
    bool TryGetAudioLevels(out MediaAudioLevels levels);
}
