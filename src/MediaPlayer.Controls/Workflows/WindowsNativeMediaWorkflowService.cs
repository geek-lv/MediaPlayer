using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MediaPlayer.Controls.Workflows;

public sealed class WindowsNativeMediaWorkflowService : DelegatingMediaWorkflowService
{
    private readonly string? _nativeHelperPath;

    public WindowsNativeMediaWorkflowService(IMediaWorkflowService fallback, string? nativeHelperPath = null)
        : base(fallback)
    {
        _nativeHelperPath = nativeHelperPath ?? Environment.GetEnvironmentVariable("MEDIAPLAYER_WINDOWS_WORKFLOW_HELPER");
    }

    public override async Task<MediaWorkflowResult> ExportAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
        CancellationToken cancellationToken = default)
    {
        return await ExportAsync(
                source,
                outputPath,
                preset,
                MediaExportOptions.FromQualityProfile(qualityProfile),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public override async Task<MediaWorkflowResult> ExportAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaExportOptions options,
        CancellationToken cancellationToken = default)
    {
        var nativeResult = await TryNativeExportAsync(source, outputPath, preset, options, cancellationToken).ConfigureAwait(false);
        return await ResolveNativeThenFallbackAsync(
                nativeResult,
                () => base.ExportAsync(source, outputPath, preset, options, cancellationToken),
                "Native Windows export failed.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public override async Task<MediaWorkflowResult> RecordAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
        CancellationToken cancellationToken = default)
    {
        return await RecordAsync(
                preset,
                outputPath,
                duration,
                MediaRecordingOptions.FromQualityProfile(qualityProfile),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public override async Task<MediaWorkflowResult> RecordAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaRecordingOptions options,
        CancellationToken cancellationToken = default)
    {
        var nativeResult = await TryNativeRecordAsync(preset, outputPath, duration, options, cancellationToken).ConfigureAwait(false);
        return await ResolveNativeThenFallbackAsync(
                nativeResult,
                () => base.RecordAsync(preset, outputPath, duration, options, cancellationToken),
                "Native Windows recording failed.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MediaWorkflowResult?> TryNativeExportAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaExportOptions options,
        CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_nativeHelperPath) || !File.Exists(_nativeHelperPath))
        {
            return null;
        }

        if (!source.IsFile || !File.Exists(source.LocalPath))
        {
            return null;
        }

        if (HasExportOverrides(options))
        {
            return null;
        }

        var qualityProfile = ResolveQualityProfile(options.QualityProfile);
        return await RunHelperCommandAsync(
                outputPath,
                psi =>
                {
                    ConfigureHelperProcess(psi, _nativeHelperPath);
                    psi.ArgumentList.Add("export");
                    psi.ArgumentList.Add("--source");
                    psi.ArgumentList.Add(source.LocalPath);
                    psi.ArgumentList.Add("--output");
                    psi.ArgumentList.Add(outputPath);
                    psi.ArgumentList.Add("--preset");
                    psi.ArgumentList.Add(preset.ToString());
                    psi.ArgumentList.Add("--qualityProfile");
                    psi.ArgumentList.Add(qualityProfile.ToString());
                },
                "Windows native helper export command failed.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MediaWorkflowResult?> TryNativeRecordAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaRecordingOptions options,
        CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        if (duration <= TimeSpan.Zero)
        {
            return MediaWorkflowResult.Fail("Recording duration must be positive.");
        }

        if (string.IsNullOrWhiteSpace(_nativeHelperPath) || !File.Exists(_nativeHelperPath))
        {
            return null;
        }

        if (HasRecordingOverrides(options))
        {
            return null;
        }

        var qualityProfile = ResolveQualityProfile(options.QualityProfile);
        return await RunHelperCommandAsync(
                outputPath,
                psi =>
                {
                    ConfigureHelperProcess(psi, _nativeHelperPath);
                    psi.ArgumentList.Add("record");
                    psi.ArgumentList.Add("--preset");
                    psi.ArgumentList.Add(preset.ToString());
                    psi.ArgumentList.Add("--duration");
                    psi.ArgumentList.Add(duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
                    psi.ArgumentList.Add("--output");
                    psi.ArgumentList.Add(outputPath);
                    psi.ArgumentList.Add("--qualityProfile");
                    psi.ArgumentList.Add(qualityProfile.ToString());
                },
                "Windows native helper record command failed.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static MediaWorkflowQualityProfile ResolveQualityProfile(MediaWorkflowQualityProfile? qualityProfile)
    {
        return qualityProfile switch
        {
            MediaWorkflowQualityProfile.Speed => MediaWorkflowQualityProfile.Speed,
            MediaWorkflowQualityProfile.Quality => MediaWorkflowQualityProfile.Quality,
            _ => MediaWorkflowQualityProfile.Balanced
        };
    }

    private static bool HasExportOverrides(MediaExportOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.AudioCodec)
               || options.AudioBitrateKbps > 0
               || options.AudioFormat.HasAnyValue
               || options.NormalizeLoudness;
    }

    private static bool HasRecordingOverrides(MediaRecordingOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.InputDeviceId)
               || !string.IsNullOrWhiteSpace(options.OutputDeviceId)
               || options.EnableSystemLoopback
               || options.EnableAcousticEchoCancellation
               || options.EnableNoiseSuppression
               || options.TargetAudioFormat.HasAnyValue;
    }

    private static async Task<MediaWorkflowResult> RunHelperCommandAsync(
        string outputPath,
        Action<ProcessStartInfo> configure,
        string fallbackFailureMessage,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        configure(psi);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return MediaWorkflowResult.Fail("Unable to start Windows native helper process.");
            }

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best effort cancellation.
                }
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode == 0 && File.Exists(outputPath))
            {
                return MediaWorkflowResult.Ok();
            }

            TryDelete(outputPath);
            return string.IsNullOrWhiteSpace(stderr)
                ? MediaWorkflowResult.Fail(fallbackFailureMessage)
                : MediaWorkflowResult.Fail(stderr.Trim());
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            return MediaWorkflowResult.Fail("Operation canceled.");
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            return MediaWorkflowResult.Fail(ex.Message);
        }
    }

    private static async Task<MediaWorkflowResult> ResolveNativeThenFallbackAsync(
        MediaWorkflowResult? nativeResult,
        Func<Task<MediaWorkflowResult>> fallbackFactory,
        string nativeFailurePrefix,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (nativeResult is null)
        {
            return await fallbackFactory().ConfigureAwait(false);
        }

        if (nativeResult.Value.Success)
        {
            return nativeResult.Value;
        }

        var fallbackResult = await fallbackFactory().ConfigureAwait(false);
        if (fallbackResult.Success)
        {
            return fallbackResult;
        }

        return MediaWorkflowResult.Fail($"{nativeFailurePrefix} {nativeResult.Value.ErrorMessage} | FFmpeg fallback failed: {fallbackResult.ErrorMessage}");
    }

    private static void ConfigureHelperProcess(ProcessStartInfo psi, string helperPath)
    {
        if (string.Equals(Path.GetExtension(helperPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            ProcessCommandResolver.ConfigureTool(psi, ProcessCommandResolver.ResolveDotnetHost());
            psi.ArgumentList.Add(helperPath);
            return;
        }

        psi.FileName = helperPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
