using System.Text;

namespace MediaPlayer.Controls.Audio;

public static class MediaAudioCapabilityFormatter
{
    public static string ToDisplayString(MediaAudioCapabilities capabilities)
    {
        if (capabilities == MediaAudioCapabilities.None)
        {
            return "None";
        }

        var builder = new StringBuilder(128);
        Append(capabilities, MediaAudioCapabilities.VolumeControl, "Volume", builder);
        Append(capabilities, MediaAudioCapabilities.MuteControl, "Mute", builder);
        Append(capabilities, MediaAudioCapabilities.AudioTrackEnumeration, "Track List", builder);
        Append(capabilities, MediaAudioCapabilities.AudioTrackSelection, "Track Select", builder);
        Append(capabilities, MediaAudioCapabilities.OutputDeviceEnumeration, "Output Device List", builder);
        Append(capabilities, MediaAudioCapabilities.OutputDeviceSelection, "Output Device Select", builder);
        Append(capabilities, MediaAudioCapabilities.InputDeviceEnumeration, "Input Device List", builder);
        Append(capabilities, MediaAudioCapabilities.InputDeviceSelection, "Input Device Select", builder);
        Append(capabilities, MediaAudioCapabilities.AudioLevelMetering, "Level Metering", builder);
        return builder.ToString();
    }

    private static void Append(
        MediaAudioCapabilities actual,
        MediaAudioCapabilities expected,
        string label,
        StringBuilder builder)
    {
        if ((actual & expected) != expected)
        {
            return;
        }

        if (builder.Length > 0)
        {
            _ = builder.Append(", ");
        }

        _ = builder.Append(label);
    }
}
