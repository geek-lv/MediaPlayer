using MediaPlayer.Native.Abstractions;

namespace MediaPlayer.Controls.Workflows;

public interface IMediaWorkflowProviderDiagnostics
{
    MediaPlayerNativeProviderDiagnostics Current { get; }
}
