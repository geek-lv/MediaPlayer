using System;
using System.Runtime.InteropServices;
using MediaPlayer.Controls.Audio;

namespace MediaPlayer.Controls.Tests.Audio;

public sealed class MediaAudioDeviceCatalogTests
{
    [Fact]
    public void ContainsDevice_UsesPlatformAppropriateIdComparison()
    {
        var devices = MediaAudioDeviceCatalog.CreateDefaultOutputDevices("test");
        var device = Assert.Single(devices);
        var exact = device.Id;
        var caseVariant = exact == exact.ToUpperInvariant()
            ? exact.ToLowerInvariant()
            : exact.ToUpperInvariant();

        var expectedCaseInsensitive =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        Assert.True(MediaAudioDeviceCatalog.ContainsDevice(devices, exact));
        Assert.Equal(expectedCaseInsensitive, MediaAudioDeviceCatalog.ContainsDevice(devices, caseVariant));

        Assert.True(MediaAudioDeviceCatalog.TryGetCanonicalDeviceId(devices, exact, out var exactCanonical));
        Assert.Equal(exact, exactCanonical);
        Assert.Equal(expectedCaseInsensitive, MediaAudioDeviceCatalog.TryGetCanonicalDeviceId(devices, caseVariant, out var canonicalFromVariant));
        if (expectedCaseInsensitive)
        {
            Assert.Equal(exact, canonicalFromVariant);
        }
    }

    [Fact]
    public void NormalizeDeviceId_ReturnsFallbackForBlankInput()
    {
        const string fallback = "default";
        Assert.Equal(fallback, MediaAudioDeviceCatalog.NormalizeDeviceId(string.Empty, fallback));
        Assert.Equal(fallback, MediaAudioDeviceCatalog.NormalizeDeviceId("   ", fallback));
        Assert.Equal("device-a", MediaAudioDeviceCatalog.NormalizeDeviceId(" device-a ", fallback));
    }
}
