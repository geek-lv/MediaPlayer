using MediaPlayer.Controls.Audio;

namespace MediaPlayer.Controls.Workflows;

public readonly record struct MediaRecordingOptions(
    MediaWorkflowQualityProfile? QualityProfile,
    string InputDeviceId,
    string OutputDeviceId,
    bool EnableSystemLoopback,
    bool EnableAcousticEchoCancellation,
    bool EnableNoiseSuppression,
    MediaAudioFormat TargetAudioFormat)
{
    public static MediaRecordingOptions FromQualityProfile(MediaWorkflowQualityProfile qualityProfile)
    {
        return new MediaRecordingOptions(
            qualityProfile,
            string.Empty,
            string.Empty,
            EnableSystemLoopback: false,
            EnableAcousticEchoCancellation: false,
            EnableNoiseSuppression: false,
            default);
    }
}
