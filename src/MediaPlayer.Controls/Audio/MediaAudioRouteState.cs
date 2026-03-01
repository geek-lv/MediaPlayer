namespace MediaPlayer.Controls.Audio;

public readonly record struct MediaAudioRouteState(
    string SelectedInputDeviceId,
    string SelectedOutputDeviceId,
    bool LoopbackEnabled);
