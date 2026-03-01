using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaPlayer.Controls.Workflows;

public abstract class DelegatingMediaWorkflowService : IMediaWorkflowService
{
    protected DelegatingMediaWorkflowService(IMediaWorkflowService fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        Fallback = fallback;
    }

    protected IMediaWorkflowService Fallback { get; }

    public virtual string GetExportPresetDisplayName(MediaExportPreset preset) => Fallback.GetExportPresetDisplayName(preset);

    public virtual string GetRecordingPresetDisplayName(MediaRecordingPreset preset) => Fallback.GetRecordingPresetDisplayName(preset);

    public virtual string GetQualityProfileDisplayName(MediaWorkflowQualityProfile profile) => Fallback.GetQualityProfileDisplayName(profile);

    public virtual string GetSuggestedExportFileName(Uri source, MediaExportPreset preset) => Fallback.GetSuggestedExportFileName(source, preset);

    public virtual string GetSuggestedRecordingFileName(MediaRecordingPreset preset, DateTime timestamp) => Fallback.GetSuggestedRecordingFileName(preset, timestamp);

    public virtual string BuildSiblingOutputPath(string outputPath, string siblingSuffix) => Fallback.BuildSiblingOutputPath(outputPath, siblingSuffix);

    public virtual Task<MediaWorkflowResult> TrimAsync(
        Uri source,
        TimeSpan startTime,
        TimeSpan endTime,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        Fallback.TrimAsync(source, startTime, endTime, outputPath, cancellationToken);

    public virtual Task<MediaWorkflowResult> SplitAsync(
        Uri source,
        TimeSpan splitTime,
        TimeSpan duration,
        string partOnePath,
        string partTwoPath,
        CancellationToken cancellationToken = default) =>
        Fallback.SplitAsync(source, splitTime, duration, partOnePath, partTwoPath, cancellationToken);

    public virtual Task<MediaWorkflowResult> CombineAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        Fallback.CombineAsync(inputPaths, outputPath, cancellationToken);

    public virtual Task<MediaWorkflowResult> RemoveAudioAsync(
        Uri source,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        Fallback.RemoveAudioAsync(source, outputPath, cancellationToken);

    public virtual Task<MediaWorkflowResult> RemoveVideoAsync(
        Uri source,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        Fallback.RemoveVideoAsync(source, outputPath, cancellationToken);

    public virtual Task<MediaWorkflowResult> TransformAsync(
        Uri source,
        string outputPath,
        MediaVideoTransform transform,
        CancellationToken cancellationToken = default) =>
        Fallback.TransformAsync(source, outputPath, transform, cancellationToken);

    public virtual Task<MediaWorkflowResult> ExportAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
        CancellationToken cancellationToken = default) =>
        Fallback.ExportAsync(source, outputPath, preset, qualityProfile, cancellationToken);

    public virtual Task<MediaWorkflowResult> ExportAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaExportOptions options,
        CancellationToken cancellationToken = default) =>
        Fallback.ExportAsync(source, outputPath, preset, options, cancellationToken);

    public virtual Task<MediaWorkflowResult> RecordAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
        CancellationToken cancellationToken = default) =>
        Fallback.RecordAsync(preset, outputPath, duration, qualityProfile, cancellationToken);

    public virtual Task<MediaWorkflowResult> RecordAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaRecordingOptions options,
        CancellationToken cancellationToken = default) =>
        Fallback.RecordAsync(preset, outputPath, duration, options, cancellationToken);
}
