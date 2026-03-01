using MediaPlayer.Native.Abstractions;

namespace MediaPlayer.Controls.Workflows;

internal sealed class MediaWorkflowProviderDiagnostics : IMediaWorkflowProviderDiagnostics
{
    public MediaPlayerNativeProviderDiagnostics Current { get; private set; } =
        new(
            MediaPlayerNativeProviderMode.AutoPreferInterop,
            MediaPlayerNativeProviderKind.Unknown,
            string.Empty);

    public void Update(MediaPlayerNativeProviderDiagnostics diagnostics)
    {
        Current = diagnostics;
    }
}
