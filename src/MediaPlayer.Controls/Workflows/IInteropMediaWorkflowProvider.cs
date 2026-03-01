using MediaPlayer.Native.Abstractions;
using MediaPlayer.Native.Interop;

namespace MediaPlayer.Controls.Workflows;

internal interface IInteropMediaWorkflowProvider
{
    MediaPlayerInteropWorkflowProviderId ProviderId { get; }

    string Name { get; }

    MediaPlayerNativeProviderKind ProviderKind { get; }

    bool IsAvailable { get; }

    string UnavailableReason { get; }

    Task<MediaWorkflowResult?> TrimAsync(
        Uri source,
        TimeSpan startTime,
        TimeSpan endTime,
        string outputPath,
        CancellationToken cancellationToken = default);

    Task<MediaWorkflowResult?> SplitAsync(
        Uri source,
        TimeSpan splitTime,
        TimeSpan duration,
        string partOnePath,
        string partTwoPath,
        CancellationToken cancellationToken = default);

    Task<MediaWorkflowResult?> CombineAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken cancellationToken = default);

    Task<MediaWorkflowResult?> RemoveAudioAsync(
        Uri source,
        string outputPath,
        CancellationToken cancellationToken = default);

    Task<MediaWorkflowResult?> RemoveVideoAsync(
        Uri source,
        string outputPath,
        CancellationToken cancellationToken = default);

    Task<MediaWorkflowResult?> TransformAsync(
        Uri source,
        string outputPath,
        MediaVideoTransform transform,
        CancellationToken cancellationToken = default);

    Task<MediaWorkflowResult?> ExportAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
        CancellationToken cancellationToken = default);

    Task<MediaWorkflowResult?> ExportAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaExportOptions options,
        CancellationToken cancellationToken = default)
    {
        return ExportAsync(source, outputPath, preset, options.QualityProfile ?? MediaWorkflowQualityProfile.Balanced, cancellationToken);
    }

    Task<MediaWorkflowResult?> RecordAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
        CancellationToken cancellationToken = default);

    Task<MediaWorkflowResult?> RecordAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaRecordingOptions options,
        CancellationToken cancellationToken = default)
    {
        return RecordAsync(preset, outputPath, duration, options.QualityProfile ?? MediaWorkflowQualityProfile.Balanced, cancellationToken);
    }
}
