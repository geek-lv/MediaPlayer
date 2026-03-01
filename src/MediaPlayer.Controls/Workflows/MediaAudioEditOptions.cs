namespace MediaPlayer.Controls.Workflows;

public readonly record struct MediaAudioEditOptions(
    bool PreserveMetadata,
    bool PreferStreamCopy)
{
    public static MediaAudioEditOptions Default => new(PreserveMetadata: true, PreferStreamCopy: true);
}
