using MediaPlayer.Controls;
using MediaPlayer.Native.Abstractions;

namespace MediaPlayer.Controls.Tests.Native;

public sealed class MediaPlayerNativeRuntimeTests
{
    [Fact]
    public void Configure_UpdatesRuntimeOptions_AndResetsDiagnostics()
    {
        var previousOptions = MediaPlayerNativeRuntime.GetOptions();
        try
        {
            MediaPlayerNativeRuntime.Configure(new MediaPlayerNativeOptions
            {
                ProviderMode = MediaPlayerNativeProviderMode.NativeBindingsOnly
            });

            var configuredOptions = MediaPlayerNativeRuntime.GetOptions();
            var diagnostics = MediaPlayerNativeRuntime.GetPlaybackDiagnostics();

            Assert.Equal(MediaPlayerNativeProviderMode.NativeBindingsOnly, configuredOptions.ProviderMode);
            Assert.Equal(MediaPlayerNativeProviderMode.NativeBindingsOnly, diagnostics.ConfiguredMode);
            Assert.Equal(MediaPlayerNativeProviderKind.Unknown, diagnostics.ActiveProvider);
            Assert.Equal(string.Empty, diagnostics.FallbackReason);
        }
        finally
        {
            MediaPlayerNativeRuntime.Configure(previousOptions);
        }
    }
}
