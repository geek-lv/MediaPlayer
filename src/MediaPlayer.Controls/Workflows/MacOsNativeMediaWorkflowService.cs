using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MediaPlayer.Controls.Workflows;

public sealed class MacOsNativeMediaWorkflowService : DelegatingMediaWorkflowService
{
    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public MacOsNativeMediaWorkflowService(IMediaWorkflowService fallback)
        : base(fallback)
    {
    }

    public override string GetSuggestedRecordingFileName(MediaRecordingPreset preset, DateTime timestamp)
    {
        var stamp = timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return preset switch
        {
            MediaRecordingPreset.Screen => $"screen-recording-{stamp}.mov",
            MediaRecordingPreset.Movie => $"movie-recording-{stamp}.mov",
            _ => base.GetSuggestedRecordingFileName(preset, timestamp)
        };
    }

    public override async Task<MediaWorkflowResult> TrimAsync(
        Uri source,
        TimeSpan startTime,
        TimeSpan endTime,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var nativeResult = await TryTrimNativeAsync(source, startTime, endTime, outputPath, cancellationToken).ConfigureAwait(false);
        return await ResolveNativeThenFallbackAsync(
                nativeResult,
                () => base.TrimAsync(source, startTime, endTime, outputPath, cancellationToken),
                "Native macOS trim failed.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public override async Task<MediaWorkflowResult> SplitAsync(
        Uri source,
        TimeSpan splitTime,
        TimeSpan duration,
        string partOnePath,
        string partTwoPath,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            return MediaWorkflowResult.Fail("Split requires known media duration.");
        }

        if (splitTime <= TimeSpan.Zero || splitTime >= duration)
        {
            return MediaWorkflowResult.Fail("Split time must be inside media duration.");
        }

        var first = await TrimAsync(source, TimeSpan.Zero, splitTime, partOnePath, cancellationToken).ConfigureAwait(false);
        if (!first.Success)
        {
            return MediaWorkflowResult.Fail($"Split failed (part 1): {first.ErrorMessage}");
        }

        var second = await TrimAsync(source, splitTime, duration, partTwoPath, cancellationToken).ConfigureAwait(false);
        if (!second.Success)
        {
            return MediaWorkflowResult.Fail($"Split failed (part 2): {second.ErrorMessage}");
        }

        return MediaWorkflowResult.Ok();
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
        var nativeResult = await TryExportNativeAsync(source, outputPath, preset, options, cancellationToken).ConfigureAwait(false);
        return await ResolveNativeThenFallbackAsync(
                nativeResult,
                () => base.ExportAsync(source, outputPath, preset, options, cancellationToken),
                "Native macOS export failed.",
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
        var nativeResult = await TryRecordNativeAsync(preset, outputPath, duration, options, cancellationToken).ConfigureAwait(false);
        return await ResolveNativeThenFallbackAsync(
                nativeResult,
                () => base.RecordAsync(preset, outputPath, duration, options, cancellationToken),
                "Native macOS recording failed.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<MediaWorkflowResult?> TryTrimNativeAsync(
        Uri source,
        TimeSpan startTime,
        TimeSpan endTime,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return null;
        }

        if (endTime <= startTime)
        {
            return MediaWorkflowResult.Fail("Trim duration must be positive.");
        }

        if (!TryResolveLocalSource(source, outputPath, out var sourcePath, out var sourceValidationError))
        {
            return sourceValidationError is null ? null : MediaWorkflowResult.Fail(sourceValidationError);
        }

        if (!IsToolAvailable("avconvert"))
        {
            return null;
        }

        var duration = endTime - startTime;
        return await RunCommandWritingOutputAsync(
                outputPath,
                psi =>
                {
                    psi.FileName = "avconvert";
                    psi.ArgumentList.Add("--source");
                    psi.ArgumentList.Add(sourcePath);
                    psi.ArgumentList.Add("--preset");
                    psi.ArgumentList.Add("PresetPassthrough");
                    psi.ArgumentList.Add("--output");
                    psi.ArgumentList.Add(outputPath);
                    psi.ArgumentList.Add("--replace");
                    if (startTime > TimeSpan.Zero)
                    {
                        psi.ArgumentList.Add("--start");
                        psi.ArgumentList.Add(startTime.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
                    }

                    psi.ArgumentList.Add("--duration");
                    psi.ArgumentList.Add(duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
                },
                "avconvert trim command failed.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<MediaWorkflowResult?> TryExportNativeAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaExportOptions options,
        CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return null;
        }

        if (!TryResolveLocalSource(source, outputPath, out var sourcePath, out var sourceValidationError))
        {
            return sourceValidationError is null ? null : MediaWorkflowResult.Fail(sourceValidationError);
        }

        if (HasExportOverrides(options))
        {
            return null;
        }

        var qualityProfile = ResolveQualityProfile(options.QualityProfile);
        if (!TryMapExportPreset(preset, qualityProfile, out var avPreset, out var useMultiPass, out var disableFastStart))
        {
            return null;
        }

        if (!IsToolAvailable("avconvert"))
        {
            return null;
        }

        return await RunCommandWritingOutputAsync(
                outputPath,
                psi =>
                {
                    psi.FileName = "avconvert";
                    psi.ArgumentList.Add("--source");
                    psi.ArgumentList.Add(sourcePath);
                    psi.ArgumentList.Add("--preset");
                    psi.ArgumentList.Add(avPreset);
                    psi.ArgumentList.Add("--output");
                    psi.ArgumentList.Add(outputPath);
                    psi.ArgumentList.Add("--replace");
                    if (disableFastStart)
                    {
                        psi.ArgumentList.Add("--disableFastStart");
                    }

                    if (useMultiPass)
                    {
                        psi.ArgumentList.Add("--multiPass");
                    }
                },
                "avconvert export command failed.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<MediaWorkflowResult?> TryRecordNativeAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaRecordingOptions options,
        CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return null;
        }

        if (duration <= TimeSpan.Zero)
        {
            return MediaWorkflowResult.Fail("Recording duration must be positive.");
        }

        if (HasRecordingOverrides(options))
        {
            return null;
        }

        if (preset != MediaRecordingPreset.Screen)
        {
            return null;
        }

        if (!IsToolAvailable("screencapture"))
        {
            return null;
        }

        var extension = Path.GetExtension(outputPath);
        var qualityProfile = ResolveQualityProfile(options.QualityProfile);
        var requiresConversion = qualityProfile != MediaWorkflowQualityProfile.Balanced
                                 || !string.Equals(extension, ".mov", StringComparison.OrdinalIgnoreCase);
        var capturePath = requiresConversion
            ? Path.Combine(
                Path.GetDirectoryName(outputPath) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(outputPath)}-native-{Guid.NewGuid():N}.mov")
            : outputPath;

        var recordResult = await RunCommandWritingOutputAsync(
                capturePath,
                psi =>
                {
                    psi.FileName = "screencapture";
                    psi.ArgumentList.Add("-v");
                    psi.ArgumentList.Add("-D");
                    psi.ArgumentList.Add("1");
                    psi.ArgumentList.Add("-V");
                    psi.ArgumentList.Add(duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
                    psi.ArgumentList.Add("-x");
                    psi.ArgumentList.Add(capturePath);
                },
                "screencapture screen recording failed.",
                cancellationToken)
            .ConfigureAwait(false);

        if (!recordResult.Success || !requiresConversion)
        {
            if (!recordResult.Success && requiresConversion)
            {
                TryDelete(capturePath);
            }

            return recordResult;
        }

        if (!IsToolAvailable("avconvert"))
        {
            TryDelete(capturePath);
            return MediaWorkflowResult.Fail("avconvert is required to convert native screen capture output.");
        }

        if (!TryMapRecordingConversionPreset(qualityProfile, out var conversionPreset, out var useMultiPass, out var disableFastStart))
        {
            TryDelete(capturePath);
            return null;
        }

        var convertResult = await RunCommandWritingOutputAsync(
                outputPath,
                psi =>
                {
                    psi.FileName = "avconvert";
                    psi.ArgumentList.Add("--source");
                    psi.ArgumentList.Add(capturePath);
                    psi.ArgumentList.Add("--preset");
                    psi.ArgumentList.Add(conversionPreset);
                    psi.ArgumentList.Add("--output");
                    psi.ArgumentList.Add(outputPath);
                    psi.ArgumentList.Add("--replace");
                    if (disableFastStart)
                    {
                        psi.ArgumentList.Add("--disableFastStart");
                    }

                    if (useMultiPass)
                    {
                        psi.ArgumentList.Add("--multiPass");
                    }
                },
                "avconvert conversion for native screen recording failed.",
                cancellationToken)
            .ConfigureAwait(false);

        TryDelete(capturePath);
        return convertResult;
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

    private static bool TryMapExportPreset(
        MediaExportPreset preset,
        MediaWorkflowQualityProfile qualityProfile,
        out string avPreset,
        out bool useMultiPass,
        out bool disableFastStart)
    {
        useMultiPass = false;
        disableFastStart = false;
        avPreset = preset switch
        {
            MediaExportPreset.Video2160p => "Preset3840x2160",
            MediaExportPreset.Video1080p => "Preset1920x1080",
            MediaExportPreset.Video720p => "Preset1280x720",
            MediaExportPreset.Video480p => "Preset640x480",
            MediaExportPreset.AudioOnly => "PresetAppleM4A",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(avPreset))
        {
            return false;
        }

        if (preset == MediaExportPreset.AudioOnly)
        {
            return true;
        }

        useMultiPass = qualityProfile == MediaWorkflowQualityProfile.Quality;
        disableFastStart = qualityProfile == MediaWorkflowQualityProfile.Speed;
        return true;
    }

    private static bool TryMapRecordingConversionPreset(
        MediaWorkflowQualityProfile qualityProfile,
        out string avPreset,
        out bool useMultiPass,
        out bool disableFastStart)
    {
        disableFastStart = qualityProfile == MediaWorkflowQualityProfile.Speed;
        useMultiPass = qualityProfile == MediaWorkflowQualityProfile.Quality;
        avPreset = qualityProfile switch
        {
            MediaWorkflowQualityProfile.Speed => "PresetMediumQuality",
            MediaWorkflowQualityProfile.Balanced => "PresetHighestQuality",
            MediaWorkflowQualityProfile.Quality => "PresetHighestQuality",
            _ => string.Empty
        };
        return !string.IsNullOrWhiteSpace(avPreset);
    }

    private static bool TryResolveLocalSource(Uri source, string outputPath, out string sourcePath, out string? validationError)
    {
        sourcePath = string.Empty;
        validationError = null;

        if (!source.IsFile)
        {
            return false;
        }

        sourcePath = source.LocalPath;
        if (!File.Exists(sourcePath))
        {
            validationError = $"Source file not found: {sourcePath}";
            return false;
        }

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath), PathComparison))
        {
            validationError = "Output file must be different from source file.";
            return false;
        }

        return true;
    }

    private static bool IsToolAvailable(string toolName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(toolName);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(1200))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(200);
                    }
                }
                catch
                {
                    // Ignore cleanup races for timed-out tool probes.
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<MediaWorkflowResult> RunCommandWritingOutputAsync(
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
                return MediaWorkflowResult.Fail("Unable to start native workflow process.");
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
