using MediaPlayer.Controls.Audio;
using MediaPlayer.Controls.Backends;
using System.Runtime.InteropServices;

namespace MediaPlayer.Controls.Tests.Backends;

public sealed class MediaAudioCapabilityProviderTests
{
    [Fact]
    public void NullBackend_ReportsNoAudioCapabilities()
    {
        using var backend = new NullMediaBackend("unavailable");
        var provider = Assert.IsAssignableFrom<IMediaAudioCapabilityProvider>(backend);
        var playback = Assert.IsAssignableFrom<IMediaAudioPlaybackController>(backend);

        Assert.Equal(MediaAudioCapabilities.None, provider.AudioCapabilities);
        Assert.False(playback.SupportsVolumeControl);
        Assert.False(playback.SupportsMuteControl);
    }

    [Fact]
    public void FfmpegBackend_ReportsCapabilitiesMatchingAudioPipelineAvailability()
    {
        using var backend = new FfmpegMediaBackend();
        var provider = Assert.IsAssignableFrom<IMediaAudioCapabilityProvider>(backend);
        var playback = Assert.IsAssignableFrom<IMediaAudioPlaybackController>(backend);
        var devices = Assert.IsAssignableFrom<IMediaAudioDeviceController>(backend);
        var audioPipelineAvailable = FfmpegMediaBackend.IsAudioPlaybackAvailable();

        Assert.Equal(audioPipelineAvailable, playback.SupportsVolumeControl);
        Assert.Equal(audioPipelineAvailable, playback.SupportsMuteControl);
        Assert.Equal(audioPipelineAvailable, provider.AudioCapabilities.HasFlag(MediaAudioCapabilities.VolumeControl));
        Assert.Equal(audioPipelineAvailable, provider.AudioCapabilities.HasFlag(MediaAudioCapabilities.MuteControl));
        Assert.True(provider.AudioCapabilities.HasFlag(MediaAudioCapabilities.InputDeviceEnumeration));
        Assert.True(provider.AudioCapabilities.HasFlag(MediaAudioCapabilities.OutputDeviceEnumeration));
        Assert.NotEmpty(devices.GetAudioInputDevices());
        Assert.NotEmpty(devices.GetAudioOutputDevices());
    }

    [Fact]
    public void FfmpegBackend_DeviceController_UsesDefaultRoutes_AndValidatesDeviceIds()
    {
        using var backend = new FfmpegMediaBackend();
        var devices = Assert.IsAssignableFrom<IMediaAudioDeviceController>(backend);
        var input = Assert.Single(devices.GetAudioInputDevices());
        var output = Assert.Single(devices.GetAudioOutputDevices());
        var route = devices.GetAudioRouteState();

        Assert.Equal(input.Id, route.SelectedInputDeviceId);
        Assert.Equal(output.Id, route.SelectedOutputDeviceId);
        Assert.True(devices.TrySetAudioInputDevice(input.Id));
        Assert.True(devices.TrySetAudioOutputDevice(output.Id));

        var caseVariantInput = input.Id == input.Id.ToUpperInvariant()
            ? input.Id.ToLowerInvariant()
            : input.Id.ToUpperInvariant();
        var caseVariantOutput = output.Id == output.Id.ToUpperInvariant()
            ? output.Id.ToLowerInvariant()
            : output.Id.ToUpperInvariant();
        var expectedCaseInsensitive =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        Assert.Equal(expectedCaseInsensitive, devices.TrySetAudioInputDevice(caseVariantInput));
        Assert.Equal(expectedCaseInsensitive, devices.TrySetAudioOutputDevice(caseVariantOutput));
        var canonicalRoute = devices.GetAudioRouteState();
        Assert.Equal(input.Id, canonicalRoute.SelectedInputDeviceId);
        Assert.Equal(output.Id, canonicalRoute.SelectedOutputDeviceId);

        Assert.False(devices.TrySetAudioInputDevice("missing-input"));
        Assert.False(devices.TrySetAudioOutputDevice("missing-output"));
    }
}
