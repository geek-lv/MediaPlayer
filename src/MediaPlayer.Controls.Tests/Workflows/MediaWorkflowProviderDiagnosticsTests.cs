using MediaPlayer.Controls.Workflows;
using MediaPlayer.Native.Abstractions;

namespace MediaPlayer.Controls.Tests.Workflows;

public sealed class MediaWorkflowProviderDiagnosticsTests
{
    [Fact]
    public void DefaultCurrent_UsesStableAutoUnknownState()
    {
        IMediaWorkflowProviderDiagnostics diagnostics = new MediaWorkflowProviderDiagnostics();

        Assert.Equal(MediaPlayerNativeProviderMode.AutoPreferInterop, diagnostics.Current.ConfiguredMode);
        Assert.Equal(MediaPlayerNativeProviderKind.Unknown, diagnostics.Current.ActiveProvider);
        Assert.Equal(string.Empty, diagnostics.Current.FallbackReason);
    }
}
