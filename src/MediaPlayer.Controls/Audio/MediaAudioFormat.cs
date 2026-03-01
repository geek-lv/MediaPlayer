namespace MediaPlayer.Controls.Audio;

public readonly record struct MediaAudioFormat(
    int SampleRate,
    int Channels,
    string SampleFormat,
    string ChannelLayout)
{
    public bool HasAnyValue =>
        SampleRate > 0
        || Channels > 0
        || !string.IsNullOrWhiteSpace(SampleFormat)
        || !string.IsNullOrWhiteSpace(ChannelLayout);
}
