using System.Collections.ObjectModel;
using MediaPlayer.Native.Interop;

namespace MediaPlayer.Controls.Workflows;

internal sealed class InteropMediaWorkflowService : DelegatingMediaWorkflowService
{
    private readonly IReadOnlyList<IInteropMediaWorkflowProvider> _providers;
    private readonly ReadOnlyCollection<MediaPlayerInteropWorkflowProviderId> _providerIds;

    public InteropMediaWorkflowService(
        FfmpegMediaWorkflowService fallback,
        IEnumerable<IInteropMediaWorkflowProvider> providers)
        : base(fallback)
    {
        var available = BuildAvailableProviderList(providers);
        _providers = available.providers;
        _providerIds = available.providerIds;
    }

    internal InteropMediaWorkflowService(
        IMediaWorkflowService fallback,
        IReadOnlyList<IInteropMediaWorkflowProvider> providers,
        IReadOnlyList<MediaPlayerInteropWorkflowProviderId> providerIds)
        : base(fallback)
    {
        _providers = providers;
        _providerIds = new ReadOnlyCollection<MediaPlayerInteropWorkflowProviderId>(providerIds.ToArray());
    }

    public bool HasRegisteredInteropProviders => _providers.Count > 0;

    internal IReadOnlyList<MediaPlayerInteropWorkflowProviderId> ActiveProviderIds => _providerIds;

    internal bool HasProvider(MediaPlayerInteropWorkflowProviderId providerId)
    {
        for (var index = 0; index < _providerIds.Count; index++)
        {
            if (_providerIds[index] == providerId)
            {
                return true;
            }
        }

        return false;
    }

    public override Task<MediaWorkflowResult> TrimAsync(
        Uri source,
        TimeSpan startTime,
        TimeSpan endTime,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(
            provider => provider.TrimAsync(source, startTime, endTime, outputPath, cancellationToken),
            () => base.TrimAsync(source, startTime, endTime, outputPath, cancellationToken));
    }

    public override Task<MediaWorkflowResult> SplitAsync(
        Uri source,
        TimeSpan splitTime,
        TimeSpan duration,
        string partOnePath,
        string partTwoPath,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(
            provider => provider.SplitAsync(source, splitTime, duration, partOnePath, partTwoPath, cancellationToken),
            () => base.SplitAsync(source, splitTime, duration, partOnePath, partTwoPath, cancellationToken));
    }

    public override Task<MediaWorkflowResult> CombineAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(
            provider => provider.CombineAsync(inputPaths, outputPath, cancellationToken),
            () => base.CombineAsync(inputPaths, outputPath, cancellationToken));
    }

    public override Task<MediaWorkflowResult> RemoveAudioAsync(
        Uri source,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(
            provider => provider.RemoveAudioAsync(source, outputPath, cancellationToken),
            () => base.RemoveAudioAsync(source, outputPath, cancellationToken));
    }

    public override Task<MediaWorkflowResult> RemoveVideoAsync(
        Uri source,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(
            provider => provider.RemoveVideoAsync(source, outputPath, cancellationToken),
            () => base.RemoveVideoAsync(source, outputPath, cancellationToken));
    }

    public override Task<MediaWorkflowResult> TransformAsync(
        Uri source,
        string outputPath,
        MediaVideoTransform transform,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(
            provider => provider.TransformAsync(source, outputPath, transform, cancellationToken),
            () => base.TransformAsync(source, outputPath, transform, cancellationToken));
    }

    public override Task<MediaWorkflowResult> ExportAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(
            provider => provider.ExportAsync(source, outputPath, preset, qualityProfile, cancellationToken),
            () => base.ExportAsync(source, outputPath, preset, qualityProfile, cancellationToken));
    }

    public override Task<MediaWorkflowResult> ExportAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaExportOptions options,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(
            provider => provider.ExportAsync(source, outputPath, preset, options, cancellationToken),
            () => base.ExportAsync(source, outputPath, preset, options, cancellationToken));
    }

    public override Task<MediaWorkflowResult> RecordAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(
            provider => provider.RecordAsync(preset, outputPath, duration, qualityProfile, cancellationToken),
            () => base.RecordAsync(preset, outputPath, duration, qualityProfile, cancellationToken));
    }

    public override Task<MediaWorkflowResult> RecordAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaRecordingOptions options,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(
            provider => provider.RecordAsync(preset, outputPath, duration, options, cancellationToken),
            () => base.RecordAsync(preset, outputPath, duration, options, cancellationToken));
    }

    private static (ReadOnlyCollection<IInteropMediaWorkflowProvider> providers, ReadOnlyCollection<MediaPlayerInteropWorkflowProviderId> providerIds) BuildAvailableProviderList(
        IEnumerable<IInteropMediaWorkflowProvider> providers)
    {
        List<IInteropMediaWorkflowProvider> availableProviders = new();
        List<MediaPlayerInteropWorkflowProviderId> ids = new();

        foreach (var provider in providers)
        {
            if (!provider.IsAvailable)
            {
                continue;
            }

            availableProviders.Add(provider);
            ids.Add(provider.ProviderId);
        }

        return (
            new ReadOnlyCollection<IInteropMediaWorkflowProvider>(availableProviders),
            new ReadOnlyCollection<MediaPlayerInteropWorkflowProviderId>(ids));
    }

    private async Task<MediaWorkflowResult> ExecuteWithFallbackAsync(
        Func<IInteropMediaWorkflowProvider, Task<MediaWorkflowResult?>> providerExecution,
        Func<Task<MediaWorkflowResult>> fallbackFactory)
    {
        MediaWorkflowResult? interopFailure = null;
        string failingProviderName = string.Empty;

        for (var index = 0; index < _providers.Count; index++)
        {
            var provider = _providers[index];
            var result = await providerExecution(provider).ConfigureAwait(false);
            if (result is null)
            {
                continue;
            }

            if (result.Value.Success)
            {
                return result.Value;
            }

            if (interopFailure is null)
            {
                interopFailure = result;
                failingProviderName = provider.Name;
            }
        }

        var fallbackResult = await fallbackFactory().ConfigureAwait(false);
        if (fallbackResult.Success || interopFailure is null)
        {
            return fallbackResult;
        }

        return MediaWorkflowResult.Fail(
            $"Interop workflow provider '{failingProviderName}' failed: {interopFailure.Value.ErrorMessage} | FFmpeg fallback failed: {fallbackResult.ErrorMessage}");
    }
}
