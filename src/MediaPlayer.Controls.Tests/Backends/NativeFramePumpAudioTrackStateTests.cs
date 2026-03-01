using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using MediaPlayer.Controls.Audio;
using MediaPlayer.Controls.Backends;

namespace MediaPlayer.Controls.Tests.Backends;

public sealed class NativeFramePumpAudioTrackStateTests
{
    [Fact]
    public void WithoutSource_AudioTracksAreEmpty_AndSelectionFails()
    {
        using var backend = new FakeNativeFramePumpBackend();

        Assert.Empty(backend.GetAudioTracks());
        Assert.False(backend.SetAudioTrack(0));
    }

    [Fact]
    public void WithSource_DefaultAudioTrackIsExposedAndSelected()
    {
        using var backend = new FakeNativeFramePumpBackend();
        SetPrivateField(backend, "_source", new Uri("file:///tmp/mock.mp4"));

        var tracks = backend.GetAudioTracks();
        var track = Assert.Single(tracks);
        Assert.Equal(0, track.Id);
        Assert.Equal("Default", track.Name);
        Assert.True(track.IsSelected);

        Assert.True(backend.SetAudioTrack(0));
        Assert.False(backend.SetAudioTrack(7));
    }

    [Fact]
    public void NativeFramePumpCapabilities_IncludeTrackFlags()
    {
        using var backend = new FakeNativeFramePumpBackend();
        var provider = Assert.IsAssignableFrom<IMediaAudioCapabilityProvider>(backend);

        Assert.True(provider.AudioCapabilities.HasFlag(MediaAudioCapabilities.VolumeControl));
        Assert.True(provider.AudioCapabilities.HasFlag(MediaAudioCapabilities.MuteControl));
        Assert.True(provider.AudioCapabilities.HasFlag(MediaAudioCapabilities.AudioTrackEnumeration));
        Assert.True(provider.AudioCapabilities.HasFlag(MediaAudioCapabilities.AudioTrackSelection));
        Assert.True(provider.AudioCapabilities.HasFlag(MediaAudioCapabilities.InputDeviceEnumeration));
        Assert.True(provider.AudioCapabilities.HasFlag(MediaAudioCapabilities.OutputDeviceEnumeration));
    }

    [Fact]
    public void NativeFramePumpDeviceController_ExposesDefaultRoutes_AndRejectsUnknownDevices()
    {
        using var backend = new FakeNativeFramePumpBackend();
        var controller = Assert.IsAssignableFrom<IMediaAudioDeviceController>(backend);

        var input = Assert.Single(controller.GetAudioInputDevices());
        var output = Assert.Single(controller.GetAudioOutputDevices());
        var initialRoute = controller.GetAudioRouteState();
        Assert.Equal(input.Id, initialRoute.SelectedInputDeviceId);
        Assert.Equal(output.Id, initialRoute.SelectedOutputDeviceId);

        Assert.True(controller.TrySetAudioInputDevice(input.Id));
        Assert.True(controller.TrySetAudioOutputDevice(output.Id));

        var caseVariantInput = input.Id == input.Id.ToUpperInvariant()
            ? input.Id.ToLowerInvariant()
            : input.Id.ToUpperInvariant();
        var caseVariantOutput = output.Id == output.Id.ToUpperInvariant()
            ? output.Id.ToLowerInvariant()
            : output.Id.ToUpperInvariant();
        var expectedCaseInsensitive =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        Assert.Equal(expectedCaseInsensitive, controller.TrySetAudioInputDevice(caseVariantInput));
        Assert.Equal(expectedCaseInsensitive, controller.TrySetAudioOutputDevice(caseVariantOutput));
        var canonicalRoute = controller.GetAudioRouteState();
        Assert.Equal(input.Id, canonicalRoute.SelectedInputDeviceId);
        Assert.Equal(output.Id, canonicalRoute.SelectedOutputDeviceId);

        Assert.False(controller.TrySetAudioInputDevice("unknown-input"));
        Assert.False(controller.TrySetAudioOutputDevice("unknown-output"));
    }

    private static void SetPrivateField<TTarget>(TTarget target, string fieldName, object? value)
    {
        var field = target!.GetType().BaseType!.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private sealed class FakeNativeFramePumpBackend : NativeFramePumpMediaBackend
    {
        protected override string ProfileName => "Test Native";

        protected override string NativeDecodeApi => "Test Decode";

        protected override string NativeRenderPath => "Test Render";

        protected override bool TryEnsureHelperReady(out string error)
        {
            error = string.Empty;
            return true;
        }

        protected override ProcessStartInfo CreateProbeProcess(Uri source)
        {
            throw new NotSupportedException();
        }

        protected override ProcessStartInfo CreatePlaybackProcess(
            Uri source,
            TimeSpan startPosition,
            double playbackRate,
            float volume,
            bool muted)
        {
            throw new NotSupportedException();
        }

        protected override ProcessStartInfo CreateSingleFrameProcess(Uri source, TimeSpan position)
        {
            throw new NotSupportedException();
        }
    }
}
