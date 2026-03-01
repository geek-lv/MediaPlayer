using System.Text;
using MediaPlayer.Controls.Workflows;
using MediaPlayer.Native.Interop;

namespace MediaPlayer.Controls.Tests.Workflows;

public sealed class InteropMediaWorkflowServiceTests
{
    [Fact]
    public async Task TrimAsync_UsesInteropProvider_ForSupportedWavSource()
    {
        using TempWorkspace workspace = new();
        string sourcePath = Path.Combine(workspace.RootDirectory, "source.wav");
        string outputPath = Path.Combine(workspace.RootDirectory, "trimmed.wav");
        CreatePcmWaveSilence(sourcePath, sampleRate: 16000, channels: 1, bitsPerSample: 16, duration: TimeSpan.FromSeconds(1.2));

        CountingWorkflowFallback fallback = new(MediaWorkflowResult.Fail("Fallback should not be called."));
        WavInteropMediaWorkflowProvider provider = new();
        InteropMediaWorkflowService service = new(
            fallback,
            new[] { provider },
            new[] { provider.ProviderId });

        MediaWorkflowResult result = await service.TrimAsync(
            new Uri(sourcePath),
            TimeSpan.FromSeconds(0.2),
            TimeSpan.FromSeconds(0.8),
            outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(0, fallback.TrimCallCount);
        Assert.True(new FileInfo(outputPath).Length < new FileInfo(sourcePath).Length);
    }

    [Fact]
    public async Task TrimAsync_UsesFallback_WhenInteropProviderDoesNotSupportSource()
    {
        using TempWorkspace workspace = new();
        string outputPath = Path.Combine(workspace.RootDirectory, "trimmed.mp4");

        CountingWorkflowFallback fallback = new(MediaWorkflowResult.Ok());
        WavInteropMediaWorkflowProvider provider = new();
        InteropMediaWorkflowService service = new(
            fallback,
            new[] { provider },
            new[] { provider.ProviderId });

        MediaWorkflowResult result = await service.TrimAsync(
            new Uri("https://example.com/media.mp4"),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, fallback.TrimCallCount);
    }

    [Fact]
    public async Task TrimAsync_WavSource_WithNonWavOutput_UsesFallback()
    {
        using TempWorkspace workspace = new();
        string sourcePath = Path.Combine(workspace.RootDirectory, "source.wav");
        string outputPath = Path.Combine(workspace.RootDirectory, "trimmed.mp4");
        CreatePcmWaveSilence(sourcePath, sampleRate: 22050, channels: 1, bitsPerSample: 16, duration: TimeSpan.FromSeconds(1));

        CountingWorkflowFallback fallback = new(MediaWorkflowResult.Ok());
        WavInteropMediaWorkflowProvider provider = new();
        InteropMediaWorkflowService service = new(
            fallback,
            new[] { provider },
            new[] { provider.ProviderId });

        MediaWorkflowResult result = await service.TrimAsync(
            new Uri(sourcePath),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(0.5),
            outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, fallback.TrimCallCount);
    }

    [Fact]
    public async Task TrimAsync_UsesFallback_WhenInteropProviderFails()
    {
        using TempWorkspace workspace = new();
        string sourcePath = Path.Combine(workspace.RootDirectory, "broken.wav");
        string outputPath = Path.Combine(workspace.RootDirectory, "trimmed.wav");
        await File.WriteAllTextAsync(sourcePath, "not-a-wave");

        CountingWorkflowFallback fallback = new(MediaWorkflowResult.Ok());
        WavInteropMediaWorkflowProvider provider = new();
        InteropMediaWorkflowService service = new(
            fallback,
            new[] { provider },
            new[] { provider.ProviderId });

        MediaWorkflowResult result = await service.TrimAsync(
            new Uri(sourcePath),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, fallback.TrimCallCount);
    }

    [Fact]
    public async Task ExportAsync_AudioOnly_WithNonWavOutput_UsesFallback()
    {
        using TempWorkspace workspace = new();
        string sourcePath = Path.Combine(workspace.RootDirectory, "source.wav");
        string outputPath = Path.Combine(workspace.RootDirectory, "audio-only.m4a");
        CreatePcmWaveSilence(sourcePath, sampleRate: 16000, channels: 1, bitsPerSample: 16, duration: TimeSpan.FromSeconds(0.8));

        CountingWorkflowFallback fallback = new(MediaWorkflowResult.Ok());
        WavInteropMediaWorkflowProvider provider = new();
        InteropMediaWorkflowService service = new(
            fallback,
            new[] { provider },
            new[] { provider.ProviderId });

        MediaWorkflowResult result = await service.ExportAsync(
            new Uri(sourcePath),
            outputPath,
            MediaExportPreset.AudioOnly);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, fallback.ExportCallCount);
    }

    [Fact]
    public async Task ExportAsync_WithOptions_PreservesQualityProfileWhenFallingBack()
    {
        using TempWorkspace workspace = new();
        string sourcePath = Path.Combine(workspace.RootDirectory, "source.wav");
        string outputPath = Path.Combine(workspace.RootDirectory, "audio-only.m4a");
        CreatePcmWaveSilence(sourcePath, sampleRate: 16000, channels: 1, bitsPerSample: 16, duration: TimeSpan.FromSeconds(0.8));

        CountingWorkflowFallback fallback = new(MediaWorkflowResult.Ok());
        WavInteropMediaWorkflowProvider provider = new();
        InteropMediaWorkflowService service = new(
            fallback,
            new[] { provider },
            new[] { provider.ProviderId });

        MediaWorkflowResult result = await service.ExportAsync(
            new Uri(sourcePath),
            outputPath,
            MediaExportPreset.AudioOnly,
            new MediaExportOptions(
                QualityProfile: MediaWorkflowQualityProfile.Quality,
                AudioCodec: string.Empty,
                AudioBitrateKbps: 0,
                AudioFormat: default,
                NormalizeLoudness: false));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, fallback.ExportCallCount);
        Assert.Equal(MediaWorkflowQualityProfile.Quality, fallback.LastExportQualityProfile);
    }

    [Fact]
    public void Constructor_TracksActiveInteropProviderIds()
    {
        CountingWorkflowFallback fallback = new(MediaWorkflowResult.Ok());
        WavInteropMediaWorkflowProvider provider = new();
        InteropMediaWorkflowService service = new(
            fallback,
            new[] { provider },
            new[] { provider.ProviderId });

        Assert.True(service.HasRegisteredInteropProviders);
        Assert.True(service.HasProvider(MediaPlayerInteropWorkflowProviderId.ManagedPcmWaveInterop));
        Assert.False(service.HasProvider(MediaPlayerInteropWorkflowProviderId.FfmpegManagedInterop));
    }

    private static void CreatePcmWaveSilence(
        string path,
        int sampleRate,
        short channels,
        short bitsPerSample,
        TimeSpan duration)
    {
        int bytesPerSample = bitsPerSample / 8;
        int blockAlign = channels * bytesPerSample;
        int byteRate = sampleRate * blockAlign;
        int sampleCount = Math.Max(1, (int)Math.Round(sampleRate * duration.TotalSeconds));
        int dataLength = sampleCount * blockAlign;

        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(0x46464952u); // RIFF
        writer.Write((uint)(4 + (8 + 16) + (8 + dataLength)));
        writer.Write(0x45564157u); // WAVE
        writer.Write(0x20746D66u); // fmt
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(0x61746164u); // data
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
    }

    private sealed class CountingWorkflowFallback(MediaWorkflowResult trimResult) : IMediaWorkflowService
    {
        private readonly MediaWorkflowResult _trimResult = trimResult;

        public int TrimCallCount { get; private set; }
        public int ExportCallCount { get; private set; }
        public MediaWorkflowQualityProfile LastExportQualityProfile { get; private set; } = MediaWorkflowQualityProfile.Balanced;
        public MediaWorkflowQualityProfile LastRecordQualityProfile { get; private set; } = MediaWorkflowQualityProfile.Balanced;

        public string GetExportPresetDisplayName(MediaExportPreset preset) => string.Empty;

        public string GetRecordingPresetDisplayName(MediaRecordingPreset preset) => string.Empty;

        public string GetQualityProfileDisplayName(MediaWorkflowQualityProfile profile) => string.Empty;

        public string GetSuggestedExportFileName(Uri source, MediaExportPreset preset) => string.Empty;

        public string GetSuggestedRecordingFileName(MediaRecordingPreset preset, DateTime timestamp) => string.Empty;

        public string BuildSiblingOutputPath(string outputPath, string siblingSuffix) => string.Empty;

        public Task<MediaWorkflowResult> TrimAsync(
            Uri source,
            TimeSpan startTime,
            TimeSpan endTime,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            TrimCallCount++;
            return Task.FromResult(_trimResult);
        }

        public Task<MediaWorkflowResult> SplitAsync(
            Uri source,
            TimeSpan splitTime,
            TimeSpan duration,
            string partOnePath,
            string partTwoPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MediaWorkflowResult.Ok());
        }

        public Task<MediaWorkflowResult> CombineAsync(
            IReadOnlyList<string> inputPaths,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MediaWorkflowResult.Ok());
        }

        public Task<MediaWorkflowResult> RemoveAudioAsync(
            Uri source,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MediaWorkflowResult.Ok());
        }

        public Task<MediaWorkflowResult> RemoveVideoAsync(
            Uri source,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MediaWorkflowResult.Ok());
        }

        public Task<MediaWorkflowResult> TransformAsync(
            Uri source,
            string outputPath,
            MediaVideoTransform transform,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MediaWorkflowResult.Ok());
        }

        public Task<MediaWorkflowResult> ExportAsync(
            Uri source,
            string outputPath,
            MediaExportPreset preset,
            MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
            CancellationToken cancellationToken = default)
        {
            ExportCallCount++;
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

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "mediaplayer-interop-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootDirectory);
        }

        public string RootDirectory { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }
}
