using MediaPlayer.Controls.Audio;

namespace MediaPlayer.Controls.Workflows;

public readonly record struct MediaExportOptions(
    MediaWorkflowQualityProfile? QualityProfile,
    string AudioCodec,
    int AudioBitrateKbps,
    MediaAudioFormat AudioFormat,
    bool NormalizeLoudness)
{
    public static MediaExportOptions FromQualityProfile(MediaWorkflowQualityProfile qualityProfile)
    {
        return new MediaExportOptions(
            qualityProfile,
            string.Empty,
            0,
            default,
            NormalizeLoudness: false);
    }
}
