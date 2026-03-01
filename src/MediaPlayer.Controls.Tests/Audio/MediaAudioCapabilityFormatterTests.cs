using MediaPlayer.Controls.Audio;

namespace MediaPlayer.Controls.Tests.Audio;

public sealed class MediaAudioCapabilityFormatterTests
{
    [Fact]
    public void ToDisplayString_None_ReturnsNone()
    {
        string value = MediaAudioCapabilityFormatter.ToDisplayString(MediaAudioCapabilities.None);
        Assert.Equal("None", value);
    }

    [Fact]
    public void ToDisplayString_MultipleFlags_UsesStableReadableOrder()
    {
        var capabilities =
            MediaAudioCapabilities.MuteControl
            | MediaAudioCapabilities.VolumeControl
            | MediaAudioCapabilities.AudioTrackSelection;

        string value = MediaAudioCapabilityFormatter.ToDisplayString(capabilities);
        Assert.Equal("Volume, Mute, Track Select", value);
    }
}
