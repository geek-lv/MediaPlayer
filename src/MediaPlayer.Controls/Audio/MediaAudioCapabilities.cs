using System;

namespace MediaPlayer.Controls.Audio;

[Flags]
public enum MediaAudioCapabilities
{
    None = 0,
    VolumeControl = 1 << 0,
    MuteControl = 1 << 1,
    AudioTrackEnumeration = 1 << 2,
    AudioTrackSelection = 1 << 3,
    OutputDeviceEnumeration = 1 << 4,
    OutputDeviceSelection = 1 << 5,
    InputDeviceEnumeration = 1 << 6,
    InputDeviceSelection = 1 << 7,
    AudioLevelMetering = 1 << 8
}
