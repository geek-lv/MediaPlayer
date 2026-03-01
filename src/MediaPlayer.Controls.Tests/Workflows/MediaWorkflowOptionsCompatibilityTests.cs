using MediaPlayer.Controls.Workflows;

namespace MediaPlayer.Controls.Tests.Workflows;

public sealed class MediaWorkflowOptionsCompatibilityTests
{
    [Fact]
    public async Task ExportOptionsOverload_DefaultInterfaceShim_ForwardsToLegacyQualityOverload()
    {
        var service = new LegacyOnlyWorkflowService();
        IMediaWorkflowService workflow = service;

        var result = await workflow.ExportAsync(
            new Uri("https://example.com/video.mp4"),
            "/tmp/output.mp4",
            MediaExportPreset.Video1080p,
            new MediaExportOptions(
                QualityProfile: MediaWorkflowQualityProfile.Quality,
                AudioCodec: string.Empty,
                AudioBitrateKbps: 0,
                AudioFormat: default,
                NormalizeLoudness: false));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(MediaWorkflowQualityProfile.Quality, service.LastExportQualityProfile);
    }

    [Fact]
    public async Task RecordOptionsOverload_DefaultInterfaceShim_ForwardsToLegacyQualityOverload()
    {
        var service = new LegacyOnlyWorkflowService();
        IMediaWorkflowService workflow = service;

        var result = await workflow.RecordAsync(
            MediaRecordingPreset.Audio,
            "/tmp/output.m4a",
            TimeSpan.FromSeconds(2),
            new MediaRecordingOptions(
                QualityProfile: MediaWorkflowQualityProfile.Speed,
                InputDeviceId: string.Empty,
                OutputDeviceId: string.Empty,
                EnableSystemLoopback: false,
                EnableAcousticEchoCancellation: false,
                EnableNoiseSuppression: false,
                TargetAudioFormat: default));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(MediaWorkflowQualityProfile.Speed, service.LastRecordQualityProfile);
    }

    private sealed class LegacyOnlyWorkflowService : IMediaWorkflowService
    {
        public MediaWorkflowQualityProfile LastExportQualityProfile { get; private set; } = MediaWorkflowQualityProfile.Balanced;
        public MediaWorkflowQualityProfile LastRecordQualityProfile { get; private set; } = MediaWorkflowQualityProfile.Balanced;

        public string GetExportPresetDisplayName(MediaExportPreset preset) => string.Empty;

        public string GetRecordingPresetDisplayName(MediaRecordingPreset preset) => string.Empty;

        public string GetQualityProfileDisplayName(MediaWorkflowQualityProfile profile) => string.Empty;

        public string GetSuggestedExportFileName(Uri source, MediaExportPreset preset) => string.Empty;

        public string GetSuggestedRecordingFileName(MediaRecordingPreset preset, DateTime timestamp) => string.Empty;

        public string BuildSiblingOutputPath(string outputPath, string siblingSuffix) => outputPath;

        public Task<MediaWorkflowResult> TrimAsync(Uri source, TimeSpan startTime, TimeSpan endTime, string outputPath, CancellationToken cancellationToken = default)
            => Task.FromResult(MediaWorkflowResult.Ok());

        public Task<MediaWorkflowResult> SplitAsync(Uri source, TimeSpan splitTime, TimeSpan duration, string partOnePath, string partTwoPath, CancellationToken cancellationToken = default)
            => Task.FromResult(MediaWorkflowResult.Ok());

        public Task<MediaWorkflowResult> CombineAsync(IReadOnlyList<string> inputPaths, string outputPath, CancellationToken cancellationToken = default)
            => Task.FromResult(MediaWorkflowResult.Ok());

        public Task<MediaWorkflowResult> RemoveAudioAsync(Uri source, string outputPath, CancellationToken cancellationToken = default)
            => Task.FromResult(MediaWorkflowResult.Ok());

        public Task<MediaWorkflowResult> RemoveVideoAsync(Uri source, string outputPath, CancellationToken cancellationToken = default)
            => Task.FromResult(MediaWorkflowResult.Ok());

        public Task<MediaWorkflowResult> TransformAsync(Uri source, string outputPath, MediaVideoTransform transform, CancellationToken cancellationToken = default)
            => Task.FromResult(MediaWorkflowResult.Ok());

        public Task<MediaWorkflowResult> ExportAsync(
            Uri source,
            string outputPath,
            MediaExportPreset preset,
            MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
            CancellationToken cancellationToken = default)
        {
            LastExportQualityProfile = qualityProfile;
            return Task.FromResult(MediaWorkflowResult.Ok());
        }

        public Task<MediaWorkflowResult> RecordAsync(
            MediaRecordingPreset preset,
            string outputPath,
            TimeSpan duration,
            MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
            CancellationToken cancellationToken = default)
        {
            LastRecordQualityProfile = qualityProfile;
            return Task.FromResult(MediaWorkflowResult.Ok());
        }
    }
}
