using MediaPlayer.Native.Abstractions;

namespace MediaPlayer.Controls.Workflows;

public sealed class MediaWorkflowServiceRegistrationOptions
{
    public bool PreferNativePlatformServices { get; set; } = true;

    public string? WindowsNativeWorkflowHelperPath { get; set; }

    public MediaPlayerNativeProviderMode NativeProviderMode { get; set; } = MediaPlayerNativeProviderMode.AutoPreferInterop;
}
