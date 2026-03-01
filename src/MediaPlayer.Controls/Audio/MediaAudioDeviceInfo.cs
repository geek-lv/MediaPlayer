namespace MediaPlayer.Controls.Audio;

public readonly record struct MediaAudioDeviceInfo(
    string Id,
    string Name,
    MediaAudioDeviceDirection Direction,
    bool IsDefault,
    bool IsAvailable,
    string BackendTag);
