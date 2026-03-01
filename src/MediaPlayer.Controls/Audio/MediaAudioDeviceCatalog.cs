using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MediaPlayer.Controls.Audio;

internal static class MediaAudioDeviceCatalog
{
    private static readonly StringComparison DeviceIdComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static string PlatformDefaultInputDeviceId =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "0"
            : "default";

    public static string PlatformDefaultOutputDeviceId => "default";

    public static IReadOnlyList<MediaAudioDeviceInfo> CreateDefaultInputDevices(string backendTag)
    {
        var id = PlatformDefaultInputDeviceId;
        return
        [
            new MediaAudioDeviceInfo(
                id,
                "System Default Input",
                MediaAudioDeviceDirection.Input,
                IsDefault: true,
                IsAvailable: true,
                backendTag)
        ];
    }

    public static IReadOnlyList<MediaAudioDeviceInfo> CreateDefaultOutputDevices(string backendTag)
    {
        var id = PlatformDefaultOutputDeviceId;
        return
        [
            new MediaAudioDeviceInfo(
                id,
                "System Default Output",
                MediaAudioDeviceDirection.Output,
                IsDefault: true,
                IsAvailable: true,
                backendTag)
        ];
    }

    public static bool ContainsDevice(IReadOnlyList<MediaAudioDeviceInfo> devices, string deviceId)
    {
        return TryGetCanonicalDeviceId(devices, deviceId, out _);
    }

    public static bool TryGetCanonicalDeviceId(
        IReadOnlyList<MediaAudioDeviceInfo> devices,
        string deviceId,
        out string canonicalDeviceId)
    {
        for (var index = 0; index < devices.Count; index++)
        {
            var candidate = devices[index].Id;
            if (string.Equals(candidate, deviceId, DeviceIdComparison))
            {
                canonicalDeviceId = candidate;
                return true;
            }
        }

        canonicalDeviceId = string.Empty;
        return false;
    }

    public static string NormalizeDeviceId(string deviceId, string fallbackDeviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return fallbackDeviceId;
        }

        return deviceId.Trim();
    }
}
