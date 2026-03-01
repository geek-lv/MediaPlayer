using System.Collections.Generic;

namespace MediaPlayer.Controls.Audio;

public interface IMediaAudioDeviceController
{
    IReadOnlyList<MediaAudioDeviceInfo> GetAudioInputDevices();

    IReadOnlyList<MediaAudioDeviceInfo> GetAudioOutputDevices();

    MediaAudioRouteState GetAudioRouteState();

    bool TrySetAudioInputDevice(string deviceId);

    bool TrySetAudioOutputDevice(string deviceId);
}
