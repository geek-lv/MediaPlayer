using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaPlayer.Controls.Audio;

namespace MediaPlayer.Controls.Workflows;

public sealed class FfmpegMediaWorkflowService : IMediaWorkflowService
{
    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public string GetExportPresetDisplayName(MediaExportPreset preset)
    {
        return preset switch
        {
            MediaExportPreset.Video2160p => "4K",
            MediaExportPreset.Video1080p => "1080p",
            MediaExportPreset.Video720p => "720p",
            MediaExportPreset.Video480p => "480p",
            _ => "Audio Only"
        };
    }

    public string GetRecordingPresetDisplayName(MediaRecordingPreset preset)
    {
        return preset switch
        {
            MediaRecordingPreset.Screen => "New Screen Recording",
            MediaRecordingPreset.Movie => "New Movie Recording",
            _ => "New Audio Recording"
        };
    }

    public string GetQualityProfileDisplayName(MediaWorkflowQualityProfile profile)
    {
        return profile switch
        {
            MediaWorkflowQualityProfile.Speed => "Speed",
            MediaWorkflowQualityProfile.Quality => "Quality",
            _ => "Balanced"
        };
    }

    public string GetSuggestedExportFileName(Uri source, MediaExportPreset preset)
    {
        var sourcePath = source.IsFile ? source.LocalPath : source.AbsolutePath;
        var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = "media";
        }

        return preset switch
        {
            MediaExportPreset.Video2160p => $"{sourceName}-4k.mp4",
            MediaExportPreset.Video1080p => $"{sourceName}-1080p.mp4",
            MediaExportPreset.Video720p => $"{sourceName}-720p.mp4",
            MediaExportPreset.Video480p => $"{sourceName}-480p.mp4",
            _ => $"{sourceName}-audio.m4a"
        };
    }

    public string GetSuggestedRecordingFileName(MediaRecordingPreset preset, DateTime timestamp)
    {
        var stamp = timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return preset switch
        {
            MediaRecordingPreset.Screen => $"screen-recording-{stamp}.mp4",
            MediaRecordingPreset.Movie => $"movie-recording-{stamp}.mp4",
            _ => $"audio-recording-{stamp}.m4a"
        };
    }

    public string BuildSiblingOutputPath(string outputPath, string siblingSuffix)
    {
        var directory = Path.GetDirectoryName(outputPath);
        var extension = Path.GetExtension(outputPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".mp4";
        }

        var fileName = Path.GetFileNameWithoutExtension(outputPath);
        if (fileName.EndsWith("-part1", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^6];
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "media";
        }

        var siblingFileName = $"{fileName}{siblingSuffix}{extension}";
        return string.IsNullOrWhiteSpace(directory)
            ? siblingFileName
            : Path.Combine(directory, siblingFileName);
    }

    public async Task<MediaWorkflowResult> TrimAsync(
        Uri source,
        TimeSpan startTime,
        TimeSpan endTime,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var segmentDuration = endTime - startTime;
        if (segmentDuration <= TimeSpan.Zero)
        {
            return MediaWorkflowResult.Fail("Trim duration must be positive.");
        }

        var sourceValue = source.IsFile ? source.LocalPath : source.ToString();
        if (source.IsFile
            && string.Equals(Path.GetFullPath(sourceValue), Path.GetFullPath(outputPath), PathComparison))
        {
            return MediaWorkflowResult.Fail("Output file must be different from source file.");
        }

        var fastCopyResult = await RunTrimCommandAsync(
                sourceValue,
                outputPath,
                startTime,
                segmentDuration,
                useStreamCopy: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (fastCopyResult.Success)
        {
            return fastCopyResult;
        }

        return await RunTrimCommandAsync(
                sourceValue,
                outputPath,
                startTime,
                segmentDuration,
                useStreamCopy: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MediaWorkflowResult> SplitAsync(
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

    public async Task<MediaWorkflowResult> CombineAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (inputPaths.Count < 2)
        {
            return MediaWorkflowResult.Fail("At least two clips are required.");
        }

        var concatListPath = Path.Combine(Path.GetTempPath(), $"mediaplayer-concat-{Guid.NewGuid():N}.txt");
        try
        {
            await using (var writer = new StreamWriter(concatListPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                for (var i = 0; i < inputPaths.Count; i++)
                {
                    var inputPath = inputPaths[i];
                    if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                    {
                        return MediaWorkflowResult.Fail($"Clip not found: {inputPath}");
                    }

                    if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), PathComparison))
                    {
                        return MediaWorkflowResult.Fail("Output file must be different from all input clips.");
                    }

                    await writer.WriteLineAsync($"file '{EscapeConcatPath(inputPath)}'").ConfigureAwait(false);
                }
            }

            var fastCopy = await RunCombineCommandAsync(concatListPath, outputPath, useStreamCopy: true, cancellationToken).ConfigureAwait(false);
            if (fastCopy.Success)
            {
                return fastCopy;
            }

            return await RunCombineCommandAsync(concatListPath, outputPath, useStreamCopy: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (File.Exists(concatListPath))
                {
                    File.Delete(concatListPath);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    public async Task<MediaWorkflowResult> RemoveAudioAsync(
        Uri source,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var sourceValue = source.IsFile ? source.LocalPath : source.ToString();
        if (source.IsFile
            && string.Equals(Path.GetFullPath(sourceValue), Path.GetFullPath(outputPath), PathComparison))
        {
            return MediaWorkflowResult.Fail("Output file must be different from source file.");
        }

        var copyResult = await RunRemoveAudioCommandAsync(sourceValue, outputPath, useStreamCopy: true, cancellationToken).ConfigureAwait(false);
        if (copyResult.Success)
        {
            return copyResult;
        }

        return await RunRemoveAudioCommandAsync(sourceValue, outputPath, useStreamCopy: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaWorkflowResult> RemoveVideoAsync(
        Uri source,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var sourceValue = source.IsFile ? source.LocalPath : source.ToString();
        if (source.IsFile
            && string.Equals(Path.GetFullPath(sourceValue), Path.GetFullPath(outputPath), PathComparison))
        {
            return MediaWorkflowResult.Fail("Output file must be different from source file.");
        }

        var copyResult = await RunRemoveVideoCommandAsync(sourceValue, outputPath, useStreamCopy: true, cancellationToken).ConfigureAwait(false);
        if (copyResult.Success)
        {
            return copyResult;
        }

        return await RunRemoveVideoCommandAsync(sourceValue, outputPath, useStreamCopy: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaWorkflowResult> TransformAsync(
        Uri source,
        string outputPath,
        MediaVideoTransform transform,
        CancellationToken cancellationToken = default)
    {
        var sourceValue = source.IsFile ? source.LocalPath : source.ToString();
        if (source.IsFile
            && string.Equals(Path.GetFullPath(sourceValue), Path.GetFullPath(outputPath), PathComparison))
        {
            return MediaWorkflowResult.Fail("Output file must be different from source file.");
        }

        var filter = transform switch
        {
            MediaVideoTransform.Rotate90Clockwise => "transpose=1",
            MediaVideoTransform.Rotate90CounterClockwise => "transpose=2",
            MediaVideoTransform.FlipHorizontal => "hflip",
            _ => "vflip"
        };

        return await RunFfmpegCommandAsync(
                outputPath,
                psi =>
                {
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(sourceValue);
                    psi.ArgumentList.Add("-map");
                    psi.ArgumentList.Add("0:v:0?");
                    psi.ArgumentList.Add("-map");
                    psi.ArgumentList.Add("0:a:0?");
                    psi.ArgumentList.Add("-dn");
                    psi.ArgumentList.Add("-vf");
                    psi.ArgumentList.Add(filter);
                    psi.ArgumentList.Add("-c:v");
                    psi.ArgumentList.Add("libx264");
                    psi.ArgumentList.Add("-preset");
                    psi.ArgumentList.Add("veryfast");
                    psi.ArgumentList.Add("-crf");
                    psi.ArgumentList.Add("20");
                    psi.ArgumentList.Add("-c:a");
                    psi.ArgumentList.Add("aac");
                    psi.ArgumentList.Add("-b:a");
                    psi.ArgumentList.Add("192k");
                    psi.ArgumentList.Add("-movflags");
                    psi.ArgumentList.Add("+faststart");
                },
                "ffmpeg transform command failed.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MediaWorkflowResult> ExportAsync(
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

    public async Task<MediaWorkflowResult> ExportAsync(
        Uri source,
        string outputPath,
        MediaExportPreset preset,
        MediaExportOptions options,
        CancellationToken cancellationToken = default)
    {
        var sourceValue = source.IsFile ? source.LocalPath : source.ToString();
        if (source.IsFile
            && string.Equals(Path.GetFullPath(sourceValue), Path.GetFullPath(outputPath), PathComparison))
        {
            return MediaWorkflowResult.Fail("Output file must be different from source file.");
        }

        var normalizedOptions = NormalizeExportOptions(options);
        if (preset == MediaExportPreset.AudioOnly)
        {
            return await RunExportAudioOnlyCommandAsync(sourceValue, outputPath, normalizedOptions, cancellationToken).ConfigureAwait(false);
        }

        var (maxWidth, maxHeight, crf) = preset switch
        {
            MediaExportPreset.Video2160p => (3840, 2160, 18),
            MediaExportPreset.Video1080p => (1920, 1080, 20),
            MediaExportPreset.Video720p => (1280, 720, 21),
            _ => (854, 480, 22)
        };

        return await RunExportVideoCommandAsync(sourceValue, outputPath, maxWidth, maxHeight, crf, normalizedOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaWorkflowResult> RecordAsync(
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

    public async Task<MediaWorkflowResult> RecordAsync(
        MediaRecordingPreset preset,
        string outputPath,
        TimeSpan duration,
        MediaRecordingOptions options,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            return MediaWorkflowResult.Fail("Recording duration must be positive.");
        }

        var normalizedOptions = NormalizeRecordingOptions(options);
        var attempts = BuildRecordingAttempts(preset, normalizedOptions);
        if (attempts.Count == 0)
        {
            return MediaWorkflowResult.Fail($"{GetRecordingPresetDisplayName(preset)} is not supported on this platform.");
        }

        var lastError = $"{GetRecordingPresetDisplayName(preset)} failed.";
        for (var i = 0; i < attempts.Count; i++)
        {
            var attempt = attempts[i];
            var result = await RunFfmpegCommandAsync(
                    outputPath,
                    psi => attempt.Configure(psi, duration, normalizedOptions),
                    $"{attempt.Label} failed.",
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Success)
            {
                return result;
            }

            lastError = result.ErrorMessage;
        }

        return MediaWorkflowResult.Fail(lastError);
    }

    private readonly record struct RecordingAttempt(string Label, Action<ProcessStartInfo, TimeSpan, MediaRecordingOptions> Configure);

    private static List<RecordingAttempt> BuildRecordingAttempts(MediaRecordingPreset preset, MediaRecordingOptions options)
    {
        var attempts = new List<RecordingAttempt>();
        var inputDevice = string.IsNullOrWhiteSpace(options.InputDeviceId) ? string.Empty : options.InputDeviceId.Trim();
        var outputDevice = string.IsNullOrWhiteSpace(options.OutputDeviceId) ? string.Empty : options.OutputDeviceId.Trim();
        var loopbackRequested = options.EnableSystemLoopback;

        if (preset == MediaRecordingPreset.Screen)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                attempts.Add(new RecordingAttempt("Screen recording (Windows gdigrab)", (psi, duration, recordingOptions) =>
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("gdigrab");
                    psi.ArgumentList.Add("-framerate");
                    psi.ArgumentList.Add("30");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add("desktop");
                    AppendVideoRecordingEncodeArgs(psi, duration, "0:v:0?", null, recordingOptions);
                }));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                attempts.Add(new RecordingAttempt("Screen recording (macOS AVFoundation capture screen)", (psi, duration, recordingOptions) =>
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("avfoundation");
                    psi.ArgumentList.Add("-framerate");
                    psi.ArgumentList.Add("30");
                    psi.ArgumentList.Add("-capture_cursor");
                    psi.ArgumentList.Add("1");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add("Capture screen 0:none");
                    AppendVideoRecordingEncodeArgs(psi, duration, "0:v:0?", null, recordingOptions);
                }));
                attempts.Add(new RecordingAttempt("Screen recording (macOS AVFoundation fallback device)", (psi, duration, recordingOptions) =>
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("avfoundation");
                    psi.ArgumentList.Add("-framerate");
                    psi.ArgumentList.Add("30");
                    psi.ArgumentList.Add("-capture_cursor");
                    psi.ArgumentList.Add("1");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add("1:none");
                    AppendVideoRecordingEncodeArgs(psi, duration, "0:v:0?", null, recordingOptions);
                }));
            }
            else
            {
                var display = Environment.GetEnvironmentVariable("DISPLAY");
                if (string.IsNullOrWhiteSpace(display))
                {
                    display = ":0.0";
                }
                else if (!display.Contains('.', StringComparison.Ordinal))
                {
                    display += ".0";
                }

                var x11Display = display;
                attempts.Add(new RecordingAttempt("Screen recording (Linux x11grab)", (psi, duration, recordingOptions) =>
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("x11grab");
                    psi.ArgumentList.Add("-framerate");
                    psi.ArgumentList.Add("30");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(x11Display);
                    AppendVideoRecordingEncodeArgs(psi, duration, "0:v:0?", null, recordingOptions);
                }));
            }

            return attempts;
        }

        if (preset == MediaRecordingPreset.Movie)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var selectedAudioInput = BuildWindowsAudioInput(inputDevice, outputDevice, loopbackRequested);
                if (!IsWindowsDefaultAudioInput(selectedAudioInput))
                {
                    attempts.Add(new RecordingAttempt("Movie recording (Windows dshow selected audio input)", (psi, duration, recordingOptions) =>
                    {
                        psi.ArgumentList.Add("-f");
                        psi.ArgumentList.Add("dshow");
                        psi.ArgumentList.Add("-i");
                        psi.ArgumentList.Add("video=default:" + selectedAudioInput);
                        AppendVideoRecordingEncodeArgs(psi, duration, "0:v:0?", "0:a:0?", recordingOptions);
                    }));
                }

                attempts.Add(new RecordingAttempt("Movie recording (Windows dshow default devices)", (psi, duration, recordingOptions) =>
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("dshow");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add("video=default:audio=default");
                    AppendVideoRecordingEncodeArgs(psi, duration, "0:v:0?", "0:a:0?", recordingOptions);
                }));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var selectedMovieInput = BuildMacOsAvFoundationMovieInput(inputDevice);
                if (!IsMacOsDefaultMovieInput(selectedMovieInput))
                {
                    attempts.Add(new RecordingAttempt("Movie recording (macOS AVFoundation selected microphone)", (psi, duration, recordingOptions) =>
                    {
                        psi.ArgumentList.Add("-f");
                        psi.ArgumentList.Add("avfoundation");
                        psi.ArgumentList.Add("-framerate");
                        psi.ArgumentList.Add("30");
                        psi.ArgumentList.Add("-i");
                        psi.ArgumentList.Add(selectedMovieInput);
                        AppendVideoRecordingEncodeArgs(psi, duration, "0:v:0?", "0:a:0?", recordingOptions);
                    }));
                }

                attempts.Add(new RecordingAttempt("Movie recording (macOS AVFoundation camera+microphone)", (psi, duration, recordingOptions) =>
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("avfoundation");
                    psi.ArgumentList.Add("-framerate");
                    psi.ArgumentList.Add("30");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add("0:0");
                    AppendVideoRecordingEncodeArgs(psi, duration, "0:v:0?", "0:a:0?", recordingOptions);
                }));
                attempts.Add(new RecordingAttempt("Movie recording (macOS AVFoundation camera only)", (psi, duration, recordingOptions) =>
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("avfoundation");
                    psi.ArgumentList.Add("-framerate");
                    psi.ArgumentList.Add("30");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add("0:none");
                    AppendVideoRecordingEncodeArgs(psi, duration, "0:v:0?", null, recordingOptions);
                }));
            }
            else
            {
                var selectedPulseInput = BuildLinuxPulseInput(inputDevice);
                if (!IsLinuxDefaultPulseInput(selectedPulseInput))
                {
                    attempts.Add(new RecordingAttempt("Movie recording (Linux v4l2 + selected pulse input)", (psi, duration, recordingOptions) =>
                    {
                        psi.ArgumentList.Add("-f");
                        psi.ArgumentList.Add("v4l2");
                        psi.ArgumentList.Add("-framerate");
                        psi.ArgumentList.Add("30");
                        psi.ArgumentList.Add("-video_size");
                        psi.ArgumentList.Add("1280x720");
                        psi.ArgumentList.Add("-i");
                        psi.ArgumentList.Add("/dev/video0");
                        psi.ArgumentList.Add("-f");
                        psi.ArgumentList.Add("pulse");
                        psi.ArgumentList.Add("-i");
                        psi.ArgumentList.Add(selectedPulseInput);
                        AppendVideoRecordingEncodeArgs(psi, duration, "0:v:0?", "1:a:0?", recordingOptions);
                    }));
                }

                attempts.Add(new RecordingAttempt("Movie recording (Linux v4l2 + pulse default)", (psi, duration, recordingOptions) =>
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("v4l2");
                    psi.ArgumentList.Add("-framerate");
                    psi.ArgumentList.Add("30");
                    psi.ArgumentList.Add("-video_size");
                    psi.ArgumentList.Add("1280x720");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add("/dev/video0");
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("pulse");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add("default");
                    AppendVideoRecordingEncodeArgs(psi, duration, "0:v:0?", "1:a:0?", recordingOptions);
                }));
                attempts.Add(new RecordingAttempt("Movie recording (Linux v4l2 camera only)", (psi, duration, recordingOptions) =>
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("v4l2");
                    psi.ArgumentList.Add("-framerate");
                    psi.ArgumentList.Add("30");
                    psi.ArgumentList.Add("-video_size");
                    psi.ArgumentList.Add("1280x720");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add("/dev/video0");
                    AppendVideoRecordingEncodeArgs(psi, duration, "0:v:0?", null, recordingOptions);
                }));
            }

            return attempts;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var selectedAudioInput = BuildWindowsAudioInput(inputDevice, outputDevice, loopbackRequested);
            if (!IsWindowsDefaultAudioInput(selectedAudioInput))
            {
                attempts.Add(new RecordingAttempt("Audio recording (Windows dshow selected input)", (psi, duration, recordingOptions) =>
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("dshow");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(selectedAudioInput);
                    AppendAudioRecordingEncodeArgs(psi, duration, "0:a:0?", recordingOptions);
                }));
            }

            attempts.Add(new RecordingAttempt("Audio recording (Windows dshow default)", (psi, duration, recordingOptions) =>
            {
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("dshow");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add("audio=default");
                AppendAudioRecordingEncodeArgs(psi, duration, "0:a:0?", recordingOptions);
            }));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            attempts.Add(new RecordingAttempt("Audio recording (macOS AVFoundation default input)", (psi, duration, recordingOptions) =>
            {
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("avfoundation");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(BuildMacOsAvFoundationAudioInput(inputDevice));
                AppendAudioRecordingEncodeArgs(psi, duration, "0:a:0?", recordingOptions);
            }));
            attempts.Add(new RecordingAttempt("Audio recording (macOS AVFoundation fallback)", (psi, duration, recordingOptions) =>
            {
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("avfoundation");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(":default");
                AppendAudioRecordingEncodeArgs(psi, duration, "0:a:0?", recordingOptions);
            }));
        }
        else
        {
            var selectedPulseInput = BuildLinuxPulseInput(inputDevice);
            if (!IsLinuxDefaultPulseInput(selectedPulseInput))
            {
                attempts.Add(new RecordingAttempt("Audio recording (Linux pulse selected input)", (psi, duration, recordingOptions) =>
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("pulse");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(selectedPulseInput);
                    AppendAudioRecordingEncodeArgs(psi, duration, "0:a:0?", recordingOptions);
                }));
            }

            attempts.Add(new RecordingAttempt("Audio recording (Linux pulse default)", (psi, duration, recordingOptions) =>
            {
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("pulse");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add("default");
                AppendAudioRecordingEncodeArgs(psi, duration, "0:a:0?", recordingOptions);
            }));
        }

        return attempts;
    }

    private static void AppendVideoRecordingEncodeArgs(
        ProcessStartInfo psi,
        TimeSpan duration,
        string videoMap,
        string? audioMap,
        MediaRecordingOptions options)
    {
        var qualityProfile = ResolveQualityProfile(options.QualityProfile);
        var settings = GetVideoRecordEncodingSettings(qualityProfile);
        var audioCodec = ResolveAudioCodec(options.TargetAudioFormat, fallbackCodec: "aac");
        var bitrateKbps = ResolveAudioBitrateKbps(options.TargetAudioFormat, settings.AudioBitrateKbps);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add(videoMap);
        if (!string.IsNullOrWhiteSpace(audioMap))
        {
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add(audioMap);
        }

        psi.ArgumentList.Add("-dn");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add(settings.Preset);
        psi.ArgumentList.Add("-crf");
        psi.ArgumentList.Add(settings.Crf.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        if (!string.IsNullOrWhiteSpace(audioMap))
        {
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add(audioCodec);
            psi.ArgumentList.Add("-b:a");
            psi.ArgumentList.Add(bitrateKbps.ToString(CultureInfo.InvariantCulture) + "k");
            AppendAudioFormatArgs(psi, options.TargetAudioFormat);
        }
        else
        {
            psi.ArgumentList.Add("-an");
        }

        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
    }

    private static void AppendAudioRecordingEncodeArgs(
        ProcessStartInfo psi,
        TimeSpan duration,
        string audioMap,
        MediaRecordingOptions options)
    {
        var qualityProfile = ResolveQualityProfile(options.QualityProfile);
        int bitrateKbps = ResolveAudioBitrateKbps(options.TargetAudioFormat, GetAudioBitrateKbps(qualityProfile));
        var audioCodec = ResolveAudioCodec(options.TargetAudioFormat, fallbackCodec: "aac");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add(audioMap);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-dn");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add(audioCodec);
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add(bitrateKbps.ToString(CultureInfo.InvariantCulture) + "k");
        AppendAudioFormatArgs(psi, options.TargetAudioFormat);
    }

    private static Task<MediaWorkflowResult> RunExportVideoCommandAsync(
        string sourceValue,
        string outputPath,
        int maxWidth,
        int maxHeight,
        int crf,
        MediaExportOptions options,
        CancellationToken cancellationToken)
    {
        var qualityProfile = ResolveQualityProfile(options.QualityProfile);
        var settings = GetVideoExportEncodingSettings(crf, qualityProfile);
        var audioCodec = ResolveAudioCodec(options.AudioFormat, fallbackCodec: "aac", options.AudioCodec);
        var bitrateKbps = ResolveAudioBitrateKbps(options.AudioFormat, settings.AudioBitrateKbps, options.AudioBitrateKbps);
        var normalizeLoudness = options.NormalizeLoudness;
        var scaleFilter = $"scale='min({maxWidth},iw)':'min({maxHeight},ih)':force_original_aspect_ratio=decrease,pad=ceil(iw/2)*2:ceil(ih/2)*2";
        return RunFfmpegCommandAsync(
            outputPath,
            psi =>
            {
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(sourceValue);
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:v:0?");
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:a:0?");
                psi.ArgumentList.Add("-dn");
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add(scaleFilter);
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add(settings.Preset);
                psi.ArgumentList.Add("-crf");
                psi.ArgumentList.Add(settings.Crf.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("-c:a");
                psi.ArgumentList.Add(audioCodec);
                psi.ArgumentList.Add("-b:a");
                psi.ArgumentList.Add(bitrateKbps.ToString(CultureInfo.InvariantCulture) + "k");
                AppendAudioFormatArgs(psi, options.AudioFormat);
                if (normalizeLoudness)
                {
                    psi.ArgumentList.Add("-af");
                    psi.ArgumentList.Add("loudnorm");
                }

                psi.ArgumentList.Add("-movflags");
                psi.ArgumentList.Add("+faststart");
            },
            "ffmpeg export command failed.",
            cancellationToken);
    }

    private static Task<MediaWorkflowResult> RunExportAudioOnlyCommandAsync(
        string sourceValue,
        string outputPath,
        MediaExportOptions options,
        CancellationToken cancellationToken)
    {
        var qualityProfile = ResolveQualityProfile(options.QualityProfile);
        int bitrateKbps = ResolveAudioBitrateKbps(options.AudioFormat, GetAudioBitrateKbps(qualityProfile), options.AudioBitrateKbps);
        var audioCodec = ResolveAudioCodec(options.AudioFormat, fallbackCodec: "aac", options.AudioCodec);
        var normalizeLoudness = options.NormalizeLoudness;
        return RunFfmpegCommandAsync(
            outputPath,
            psi =>
            {
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(sourceValue);
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:a:0?");
                psi.ArgumentList.Add("-vn");
                psi.ArgumentList.Add("-dn");
                psi.ArgumentList.Add("-c:a");
                psi.ArgumentList.Add(audioCodec);
                psi.ArgumentList.Add("-b:a");
                psi.ArgumentList.Add(bitrateKbps.ToString(CultureInfo.InvariantCulture) + "k");
                AppendAudioFormatArgs(psi, options.AudioFormat);
                if (normalizeLoudness)
                {
                    psi.ArgumentList.Add("-af");
                    psi.ArgumentList.Add("loudnorm");
                }
            },
            "ffmpeg audio export command failed.",
            cancellationToken);
    }

    private static VideoEncodingSettings GetVideoExportEncodingSettings(int baseCrf, MediaWorkflowQualityProfile qualityProfile)
    {
        return qualityProfile switch
        {
            MediaWorkflowQualityProfile.Speed => new VideoEncodingSettings("veryfast", Math.Min(baseCrf + 2, 30), 128),
            MediaWorkflowQualityProfile.Quality => new VideoEncodingSettings("slow", Math.Max(baseCrf - 2, 14), 256),
            _ => new VideoEncodingSettings("medium", baseCrf, 192)
        };
    }

    private static VideoEncodingSettings GetVideoRecordEncodingSettings(MediaWorkflowQualityProfile qualityProfile)
    {
        return qualityProfile switch
        {
            MediaWorkflowQualityProfile.Speed => new VideoEncodingSettings("ultrafast", 26, 128),
            MediaWorkflowQualityProfile.Quality => new VideoEncodingSettings("medium", 20, 192),
            _ => new VideoEncodingSettings("veryfast", 23, 160)
        };
    }

    private static int GetAudioBitrateKbps(MediaWorkflowQualityProfile qualityProfile)
    {
        return qualityProfile switch
        {
            MediaWorkflowQualityProfile.Speed => 128,
            MediaWorkflowQualityProfile.Quality => 256,
            _ => 192
        };
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

    private static MediaExportOptions NormalizeExportOptions(MediaExportOptions options)
    {
        return options with
        {
            QualityProfile = ResolveQualityProfile(options.QualityProfile),
            AudioCodec = string.IsNullOrWhiteSpace(options.AudioCodec) ? string.Empty : options.AudioCodec.Trim()
        };
    }

    private static MediaRecordingOptions NormalizeRecordingOptions(MediaRecordingOptions options)
    {
        return options with
        {
            QualityProfile = ResolveQualityProfile(options.QualityProfile),
            InputDeviceId = string.IsNullOrWhiteSpace(options.InputDeviceId) ? string.Empty : options.InputDeviceId.Trim(),
            OutputDeviceId = string.IsNullOrWhiteSpace(options.OutputDeviceId) ? string.Empty : options.OutputDeviceId.Trim()
        };
    }

    private static string BuildWindowsAudioInput(string inputDevice, string outputDevice, bool loopbackRequested)
    {
        if (!string.IsNullOrWhiteSpace(inputDevice))
        {
            return "audio=" + inputDevice;
        }

        if (loopbackRequested && !string.IsNullOrWhiteSpace(outputDevice))
        {
            return "audio=" + outputDevice;
        }

        return "audio=default";
    }

    private static string BuildMacOsAvFoundationAudioInput(string inputDevice)
    {
        if (string.IsNullOrWhiteSpace(inputDevice))
        {
            return ":0";
        }

        if (inputDevice.StartsWith(":", StringComparison.Ordinal))
        {
            return inputDevice;
        }

        return ":" + inputDevice;
    }

    private static string BuildMacOsAvFoundationMovieInput(string inputDevice)
    {
        if (string.IsNullOrWhiteSpace(inputDevice))
        {
            return "0:0";
        }

        if (inputDevice.Contains(':', StringComparison.Ordinal))
        {
            return inputDevice;
        }

        return "0:" + inputDevice;
    }

    private static string BuildLinuxPulseInput(string inputDevice)
    {
        return string.IsNullOrWhiteSpace(inputDevice) ? "default" : inputDevice;
    }

    private static bool IsWindowsDefaultAudioInput(string audioInput)
    {
        return string.Equals(audioInput, "audio=default", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMacOsDefaultMovieInput(string movieInput)
    {
        return string.Equals(movieInput, "0:0", StringComparison.Ordinal);
    }

    private static bool IsLinuxDefaultPulseInput(string pulseInput)
    {
        return string.Equals(pulseInput, "default", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAudioCodec(MediaAudioFormat audioFormat, string fallbackCodec, string explicitCodec = "")
    {
        if (!string.IsNullOrWhiteSpace(explicitCodec))
        {
            return explicitCodec.Trim();
        }

        if (!string.IsNullOrWhiteSpace(audioFormat.SampleFormat))
        {
            var format = audioFormat.SampleFormat.Trim();
            if (format.Contains("pcm", StringComparison.OrdinalIgnoreCase))
            {
                return "pcm_s16le";
            }
        }

        return fallbackCodec;
    }

    private static int ResolveAudioBitrateKbps(MediaAudioFormat audioFormat, int fallbackBitrateKbps, int explicitBitrateKbps = 0)
    {
        if (explicitBitrateKbps > 0)
        {
            return explicitBitrateKbps;
        }

        if (audioFormat.SampleRate > 0 && audioFormat.Channels > 0)
        {
            var calculated = (audioFormat.SampleRate * audioFormat.Channels * 16) / 1000;
            return Math.Clamp(calculated, 96, 320);
        }

        return fallbackBitrateKbps;
    }

    private static void AppendAudioFormatArgs(ProcessStartInfo psi, MediaAudioFormat audioFormat)
    {
        if (audioFormat.SampleRate > 0)
        {
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add(audioFormat.SampleRate.ToString(CultureInfo.InvariantCulture));
        }

        if (audioFormat.Channels > 0)
        {
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add(audioFormat.Channels.ToString(CultureInfo.InvariantCulture));
        }
    }

    private readonly record struct VideoEncodingSettings(string Preset, int Crf, int AudioBitrateKbps);

    private static Task<MediaWorkflowResult> RunRemoveAudioCommandAsync(
        string sourceValue,
        string outputPath,
        bool useStreamCopy,
        CancellationToken cancellationToken)
    {
        return RunFfmpegCommandAsync(
            outputPath,
            psi =>
            {
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(sourceValue);
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:v?");
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:s?");
                psi.ArgumentList.Add("-an");
                psi.ArgumentList.Add("-dn");

                if (useStreamCopy)
                {
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("copy");
                }
                else
                {
                    psi.ArgumentList.Add("-c:v");
                    psi.ArgumentList.Add("libx264");
                    psi.ArgumentList.Add("-preset");
                    psi.ArgumentList.Add("veryfast");
                    psi.ArgumentList.Add("-c:s");
                    psi.ArgumentList.Add("copy");
                    psi.ArgumentList.Add("-movflags");
                    psi.ArgumentList.Add("+faststart");
                }
            },
            "ffmpeg remove-audio command failed.",
            cancellationToken);
    }

    private static Task<MediaWorkflowResult> RunRemoveVideoCommandAsync(
        string sourceValue,
        string outputPath,
        bool useStreamCopy,
        CancellationToken cancellationToken)
    {
        return RunFfmpegCommandAsync(
            outputPath,
            psi =>
            {
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(sourceValue);
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:a?");
                psi.ArgumentList.Add("-vn");

                if (useStreamCopy)
                {
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("copy");
                }
                else
                {
                    psi.ArgumentList.Add("-c:a");
                    psi.ArgumentList.Add("aac");
                    psi.ArgumentList.Add("-b:a");
                    psi.ArgumentList.Add("192k");
                }
            },
            "ffmpeg remove-video command failed.",
            cancellationToken);
    }

    private static Task<MediaWorkflowResult> RunCombineCommandAsync(
        string concatListPath,
        string outputPath,
        bool useStreamCopy,
        CancellationToken cancellationToken)
    {
        return RunFfmpegCommandAsync(
            outputPath,
            psi =>
            {
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("concat");
                psi.ArgumentList.Add("-safe");
                psi.ArgumentList.Add("0");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(concatListPath);

                if (useStreamCopy)
                {
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("copy");
                }
                else
                {
                    psi.ArgumentList.Add("-c:v");
                    psi.ArgumentList.Add("libx264");
                    psi.ArgumentList.Add("-preset");
                    psi.ArgumentList.Add("veryfast");
                    psi.ArgumentList.Add("-c:a");
                    psi.ArgumentList.Add("aac");
                    psi.ArgumentList.Add("-movflags");
                    psi.ArgumentList.Add("+faststart");
                }
            },
            "ffmpeg combine command failed.",
            cancellationToken);
    }

    private static Task<MediaWorkflowResult> RunTrimCommandAsync(
        string sourceValue,
        string outputPath,
        TimeSpan startTime,
        TimeSpan duration,
        bool useStreamCopy,
        CancellationToken cancellationToken)
    {
        return RunFfmpegCommandAsync(
            outputPath,
            psi =>
            {
                psi.ArgumentList.Add("-ss");
                psi.ArgumentList.Add(startTime.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(sourceValue);
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
                if (useStreamCopy)
                {
                    psi.ArgumentList.Add("-map");
                    psi.ArgumentList.Add("0:v?");
                    psi.ArgumentList.Add("-map");
                    psi.ArgumentList.Add("0:a?");
                    psi.ArgumentList.Add("-map");
                    psi.ArgumentList.Add("0:s?");
                    psi.ArgumentList.Add("-dn");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("copy");
                }
                else
                {
                    psi.ArgumentList.Add("-map");
                    psi.ArgumentList.Add("0:v?");
                    psi.ArgumentList.Add("-map");
                    psi.ArgumentList.Add("0:a?");
                    psi.ArgumentList.Add("-c:v");
                    psi.ArgumentList.Add("libx264");
                    psi.ArgumentList.Add("-preset");
                    psi.ArgumentList.Add("veryfast");
                    psi.ArgumentList.Add("-c:a");
                    psi.ArgumentList.Add("aac");
                    psi.ArgumentList.Add("-movflags");
                    psi.ArgumentList.Add("+faststart");
                }
            },
            "ffmpeg trim command failed.",
            cancellationToken);
    }

    private static async Task<MediaWorkflowResult> RunFfmpegCommandAsync(
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
        ProcessCommandResolver.ConfigureTool(psi, ProcessCommandResolver.ResolveFfmpegExecutable());

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        configure(psi);
        psi.ArgumentList.Add(outputPath);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return MediaWorkflowResult.Fail("Unable to start ffmpeg process.");
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

            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode == 0 && File.Exists(outputPath))
            {
                return MediaWorkflowResult.Ok();
            }

            TryDeleteOutput(outputPath);
            return string.IsNullOrWhiteSpace(stderr)
                ? MediaWorkflowResult.Fail(fallbackFailureMessage)
                : MediaWorkflowResult.Fail(stderr.Trim());
        }
        catch (OperationCanceledException)
        {
            TryDeleteOutput(outputPath);
            return MediaWorkflowResult.Fail("Operation canceled.");
        }
        catch (Exception ex)
        {
            TryDeleteOutput(outputPath);
            return MediaWorkflowResult.Fail(ex.Message);
        }
    }

    private static void TryDeleteOutput(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
        catch
        {
            // Best effort cleanup of incomplete output.
        }
    }

    private static string EscapeConcatPath(string inputPath)
    {
        var normalized = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? inputPath.Replace('\\', '/')
            : inputPath;
        return normalized.Replace("'", "'\\''", StringComparison.Ordinal);
    }
}
