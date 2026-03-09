using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using MediaPlayer.Controls;
using MediaPlayer.Controls.Workflows;
using Xunit;

namespace MediaPlayer.Controls.Tests.Workflows;

public sealed class FfmpegMediaWorkflowServiceTests
{
    [Fact]
    public void DisplayAndSuggestionHelpers_ReturnExpectedValues()
    {
        FfmpegMediaWorkflowService service = new();

        Assert.Equal("4K", service.GetExportPresetDisplayName(MediaExportPreset.Video2160p));
        Assert.Equal("New Audio Recording", service.GetRecordingPresetDisplayName(MediaRecordingPreset.Audio));
        Assert.Equal("Balanced", service.GetQualityProfileDisplayName(MediaWorkflowQualityProfile.Balanced));
        Assert.Equal("trailer-720p.mp4", service.GetSuggestedExportFileName(new Uri("file:///tmp/trailer.mov"), MediaExportPreset.Video720p));
        Assert.Equal("media-audio.m4a", service.GetSuggestedExportFileName(new Uri("https://example.com/"), MediaExportPreset.AudioOnly));

        DateTime timestamp = new(2026, 3, 1, 14, 5, 6, DateTimeKind.Utc);
        Assert.Equal("screen-recording-20260301-140506.mp4", service.GetSuggestedRecordingFileName(MediaRecordingPreset.Screen, timestamp));
    }

    [Fact]
    public void BuildSiblingOutputPath_ReturnsExpectedPathShapes()
    {
        FfmpegMediaWorkflowService service = new();

        string withDirectory = service.BuildSiblingOutputPath(Path.Combine("clips", "sample-part1.mp4"), "-part2");
        Assert.Equal(Path.Combine("clips", "sample-part2.mp4"), withDirectory);

        string noExtension = service.BuildSiblingOutputPath("capture", "-part2");
        Assert.Equal("capture-part2.mp4", noExtension);
    }

    [Fact]
    public async Task TrimAsync_UsesExpectedFfmpegArguments_WhenStreamCopySucceeds()
    {
        using FakeFfmpegEnvironment fake = new(touchOutputOnSuccess: true, exitCode: 0, createPartialOutput: false, failFirstAttemptOnly: false);
        FfmpegMediaWorkflowService service = new();

        string sourcePath = Path.Combine(fake.RootDirectory, "input.mp4");
        await File.WriteAllTextAsync(sourcePath, "source");
        string outputPath = Path.Combine(fake.RootDirectory, "trimmed.mp4");

        MediaWorkflowResult result = await service.TrimAsync(new Uri(sourcePath), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(outputPath));

        IReadOnlyList<IReadOnlyList<string>> calls = fake.ReadCalls();
        IReadOnlyList<string> firstCall = Assert.Single(calls);

        Assert.Contains("-hide_banner", firstCall);
        Assert.Contains("-y", firstCall);
        AssertArgumentPair(firstCall, "-ss", "1");
        AssertArgumentPair(firstCall, "-t", "2");
        AssertArgumentPair(firstCall, "-c", "copy");
        Assert.Contains(sourcePath, firstCall);
        Assert.Equal(outputPath, firstCall[firstCall.Count - 1]);
    }

    [Fact]
    public async Task RemoveAudioAsync_DeletesIncompleteOutput_WhenAllAttemptsFail()
    {
        using FakeFfmpegEnvironment fake = new(touchOutputOnSuccess: false, exitCode: 1, createPartialOutput: true, failFirstAttemptOnly: false);
        FfmpegMediaWorkflowService service = new();

        string sourcePath = Path.Combine(fake.RootDirectory, "input.mp4");
        await File.WriteAllTextAsync(sourcePath, "source");
        string outputPath = Path.Combine(fake.RootDirectory, "without-audio.mp4");

        MediaWorkflowResult result = await service.RemoveAudioAsync(new Uri(sourcePath), outputPath);

        Assert.False(result.Success);
        Assert.Equal("ffmpeg remove-audio command failed.", result.ErrorMessage);
        Assert.False(File.Exists(outputPath));

        IReadOnlyList<IReadOnlyList<string>> calls = fake.ReadCalls();
        Assert.Equal(2, calls.Count);
        AssertArgumentPair(calls[0], "-c", "copy");
        AssertArgumentPair(calls[1], "-c:v", "libx264");
    }

    [Fact]
    public async Task TrimAsync_FallsBackToReencode_WhenFirstAttemptFails()
    {
        using FakeFfmpegEnvironment fake = new(touchOutputOnSuccess: true, exitCode: 0, createPartialOutput: true, failFirstAttemptOnly: true);
        FfmpegMediaWorkflowService service = new();

        string sourcePath = Path.Combine(fake.RootDirectory, "input.mp4");
        await File.WriteAllTextAsync(sourcePath, "source");
        string outputPath = Path.Combine(fake.RootDirectory, "trimmed.mp4");

        MediaWorkflowResult result = await service.TrimAsync(new Uri(sourcePath), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), outputPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(outputPath));

        IReadOnlyList<IReadOnlyList<string>> calls = fake.ReadCalls();
        Assert.Equal(2, calls.Count);
        AssertArgumentPair(calls[0], "-c", "copy");
        AssertArgumentPair(calls[1], "-c:v", "libx264");
        AssertArgumentPair(calls[1], "-c:a", "aac");
    }

    [Fact]
    public async Task TransformAsync_UsesExpectedVideoFilter()
    {
        using FakeFfmpegEnvironment fake = new(touchOutputOnSuccess: true, exitCode: 0, createPartialOutput: false, failFirstAttemptOnly: false);
        FfmpegMediaWorkflowService service = new();

        string sourcePath = Path.Combine(fake.RootDirectory, "input.mp4");
        await File.WriteAllTextAsync(sourcePath, "source");
        string outputPath = Path.Combine(fake.RootDirectory, "rotated.mp4");

        MediaWorkflowResult result = await service.TransformAsync(new Uri(sourcePath), outputPath, MediaVideoTransform.Rotate90Clockwise);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(outputPath));

        IReadOnlyList<IReadOnlyList<string>> calls = fake.ReadCalls();
        IReadOnlyList<string> firstCall = Assert.Single(calls);
        AssertArgumentPair(firstCall, "-vf", "transpose=1");
        AssertArgumentPair(firstCall, "-c:v", "libx264");
        AssertArgumentPair(firstCall, "-c:a", "aac");
    }

    [Fact]
    public async Task ExportAsync_QualityProfile_AffectsEncoderSettings()
    {
        using FakeFfmpegEnvironment fake = new(touchOutputOnSuccess: true, exitCode: 0, createPartialOutput: false, failFirstAttemptOnly: false);
        FfmpegMediaWorkflowService service = new();

        string sourcePath = Path.Combine(fake.RootDirectory, "input.mp4");
        await File.WriteAllTextAsync(sourcePath, "source");

        string speedOutputPath = Path.Combine(fake.RootDirectory, "speed.mp4");
        MediaWorkflowResult speedResult = await service.ExportAsync(
            new Uri(sourcePath),
            speedOutputPath,
            MediaExportPreset.Video1080p,
            MediaWorkflowQualityProfile.Speed);
        Assert.True(speedResult.Success, speedResult.ErrorMessage);

        string qualityOutputPath = Path.Combine(fake.RootDirectory, "quality.mp4");
        MediaWorkflowResult qualityResult = await service.ExportAsync(
            new Uri(sourcePath),
            qualityOutputPath,
            MediaExportPreset.Video1080p,
            MediaWorkflowQualityProfile.Quality);
        Assert.True(qualityResult.Success, qualityResult.ErrorMessage);

        IReadOnlyList<IReadOnlyList<string>> calls = fake.ReadCalls();
        Assert.Equal(2, calls.Count);
        AssertArgumentPair(calls[0], "-preset", "veryfast");
        AssertArgumentPair(calls[0], "-crf", "22");
        AssertArgumentPair(calls[0], "-b:a", "128k");
        AssertArgumentPair(calls[1], "-preset", "slow");
        AssertArgumentPair(calls[1], "-crf", "18");
        AssertArgumentPair(calls[1], "-b:a", "256k");
    }

    [Fact]
    public async Task RecordAsync_Audio_QualityProfile_AffectsBitrate()
    {
        using FakeFfmpegEnvironment fake = new(touchOutputOnSuccess: true, exitCode: 0, createPartialOutput: false, failFirstAttemptOnly: false);
        FfmpegMediaWorkflowService service = new();

        string speedOutputPath = Path.Combine(fake.RootDirectory, "audio-speed.m4a");
        MediaWorkflowResult speedResult = await service.RecordAsync(
            MediaRecordingPreset.Audio,
            speedOutputPath,
            TimeSpan.FromSeconds(2),
            MediaWorkflowQualityProfile.Speed);
        Assert.True(speedResult.Success, speedResult.ErrorMessage);

        string qualityOutputPath = Path.Combine(fake.RootDirectory, "audio-quality.m4a");
        MediaWorkflowResult qualityResult = await service.RecordAsync(
            MediaRecordingPreset.Audio,
            qualityOutputPath,
            TimeSpan.FromSeconds(2),
            MediaWorkflowQualityProfile.Quality);
        Assert.True(qualityResult.Success, qualityResult.ErrorMessage);

        IReadOnlyList<IReadOnlyList<string>> calls = fake.ReadCalls();
        Assert.Equal(2, calls.Count);
        AssertArgumentPair(calls[0], "-b:a", "128k");
        AssertArgumentPair(calls[1], "-b:a", "256k");
    }

    [Fact]
    public async Task ExportAsync_WithOptions_AppliesAudioCodecFormatAndLoudnessNormalization()
    {
        using FakeFfmpegEnvironment fake = new(touchOutputOnSuccess: true, exitCode: 0, createPartialOutput: false, failFirstAttemptOnly: false);
        FfmpegMediaWorkflowService service = new();

        string sourcePath = Path.Combine(fake.RootDirectory, "input.mp4");
        await File.WriteAllTextAsync(sourcePath, "source");
        string outputPath = Path.Combine(fake.RootDirectory, "custom-export.mp4");
        var options = new MediaExportOptions(
            QualityProfile: MediaWorkflowQualityProfile.Quality,
            AudioCodec: "libopus",
            AudioBitrateKbps: 160,
            AudioFormat: new MediaPlayer.Controls.Audio.MediaAudioFormat(48000, 2, "fltp", "stereo"),
            NormalizeLoudness: true);

        MediaWorkflowResult result = await service.ExportAsync(
            new Uri(sourcePath),
            outputPath,
            MediaExportPreset.Video1080p,
            options);

        Assert.True(result.Success, result.ErrorMessage);

        IReadOnlyList<IReadOnlyList<string>> calls = fake.ReadCalls();
        IReadOnlyList<string> firstCall = Assert.Single(calls);
        AssertArgumentPair(firstCall, "-c:a", "libopus");
        AssertArgumentPair(firstCall, "-b:a", "160k");
        AssertArgumentPair(firstCall, "-ar", "48000");
        AssertArgumentPair(firstCall, "-ac", "2");
        AssertArgumentPair(firstCall, "-af", "loudnorm");
    }

    [Fact]
    public async Task ExportAsync_WithDefaultOptions_DoesNotThrowAndUsesDefaults()
    {
        using FakeFfmpegEnvironment fake = new(touchOutputOnSuccess: true, exitCode: 0, createPartialOutput: false, failFirstAttemptOnly: false);
        FfmpegMediaWorkflowService service = new();

        string sourcePath = Path.Combine(fake.RootDirectory, "input.mp4");
        await File.WriteAllTextAsync(sourcePath, "source");
        string outputPath = Path.Combine(fake.RootDirectory, "default-options-export.mp4");

        MediaWorkflowResult result = await service.ExportAsync(
            new Uri(sourcePath),
            outputPath,
            MediaExportPreset.Video720p,
            default(MediaExportOptions));

        Assert.True(result.Success, result.ErrorMessage);
        IReadOnlyList<IReadOnlyList<string>> calls = fake.ReadCalls();
        IReadOnlyList<string> firstCall = Assert.Single(calls);
        AssertArgumentPair(firstCall, "-c:a", "aac");
    }

    [Fact]
    public async Task RecordAsync_WithOptions_AppliesInputDeviceAndTargetAudioFormat()
    {
        using FakeFfmpegEnvironment fake = new(touchOutputOnSuccess: true, exitCode: 0, createPartialOutput: false, failFirstAttemptOnly: false);
        FfmpegMediaWorkflowService service = new();

        string outputPath = Path.Combine(fake.RootDirectory, "audio-custom.m4a");
        var options = new MediaRecordingOptions(
            QualityProfile: MediaWorkflowQualityProfile.Balanced,
            InputDeviceId: "custom-input",
            OutputDeviceId: string.Empty,
            EnableSystemLoopback: false,
            EnableAcousticEchoCancellation: false,
            EnableNoiseSuppression: false,
            TargetAudioFormat: new MediaPlayer.Controls.Audio.MediaAudioFormat(44100, 1, "pcm_s16le", "mono"));

        MediaWorkflowResult result = await service.RecordAsync(
            MediaRecordingPreset.Audio,
            outputPath,
            TimeSpan.FromSeconds(1.5),
            options);

        Assert.True(result.Success, result.ErrorMessage);

        IReadOnlyList<IReadOnlyList<string>> calls = fake.ReadCalls();
        IReadOnlyList<string> firstCall = Assert.Single(calls);
        AssertArgumentPair(firstCall, "-ar", "44100");
        AssertArgumentPair(firstCall, "-ac", "1");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AssertArgumentPair(firstCall, "-i", "audio=custom-input");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            AssertArgumentPair(firstCall, "-i", ":custom-input");
        }
        else
        {
            AssertArgumentPair(firstCall, "-i", "custom-input");
        }
    }

    [Fact]
    public async Task RecordAsync_WithDefaultOptions_DoesNotThrowAndUsesDefaultInput()
    {
        using FakeFfmpegEnvironment fake = new(touchOutputOnSuccess: true, exitCode: 0, createPartialOutput: false, failFirstAttemptOnly: false);
        FfmpegMediaWorkflowService service = new();

        string outputPath = Path.Combine(fake.RootDirectory, "audio-default-options.m4a");
        MediaWorkflowResult result = await service.RecordAsync(
            MediaRecordingPreset.Audio,
            outputPath,
            TimeSpan.FromSeconds(1),
            default(MediaRecordingOptions));

        Assert.True(result.Success, result.ErrorMessage);

        IReadOnlyList<IReadOnlyList<string>> calls = fake.ReadCalls();
        IReadOnlyList<string> firstCall = Assert.Single(calls);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AssertArgumentPair(firstCall, "-i", "audio=default");
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            AssertArgumentPair(firstCall, "-i", ":0");
            return;
        }

        AssertArgumentPair(firstCall, "-i", "default");
    }

    [Fact]
    public async Task RecordAsync_Movie_WithSelectedInput_FallsBackToDefaultDeviceAttempt()
    {
        using FakeFfmpegEnvironment fake = new(touchOutputOnSuccess: true, exitCode: 0, createPartialOutput: false, failFirstAttemptOnly: true);
        FfmpegMediaWorkflowService service = new();

        string outputPath = Path.Combine(fake.RootDirectory, "movie-custom.mp4");
        var options = new MediaRecordingOptions(
            QualityProfile: MediaWorkflowQualityProfile.Balanced,
            InputDeviceId: "custom-input",
            OutputDeviceId: string.Empty,
            EnableSystemLoopback: false,
            EnableAcousticEchoCancellation: false,
            EnableNoiseSuppression: false,
            TargetAudioFormat: default);

        MediaWorkflowResult result = await service.RecordAsync(
            MediaRecordingPreset.Movie,
            outputPath,
            TimeSpan.FromSeconds(1.2),
            options);

        Assert.True(result.Success, result.ErrorMessage);

        IReadOnlyList<IReadOnlyList<string>> calls = fake.ReadCalls();
        Assert.True(calls.Count >= 2);
        IReadOnlyList<string> fallbackCall = calls[1];

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AssertArgumentPair(fallbackCall, "-i", "video=default:audio=default");
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            AssertArgumentPair(fallbackCall, "-i", "0:0");
            return;
        }

        Assert.True(ContainsOptionValuePair(fallbackCall, "-i", "default"));
    }

    private static void AssertArgumentPair(IReadOnlyList<string> args, string option, string expectedValue)
    {
        int index = IndexOf(args, option);
        Assert.True(index >= 0, FormattableString.Invariant($"Expected argument '{option}' was not present."));
        Assert.True(index + 1 < args.Count, FormattableString.Invariant($"Argument '{option}' was present without a value."));
        Assert.Equal(expectedValue, args[index + 1]);
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool ContainsOptionValuePair(IReadOnlyList<string> args, string option, string expectedValue)
    {
        for (int index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal)
                && string.Equals(args[index + 1], expectedValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FakeFfmpegEnvironment : IDisposable
    {
        private const string CallDelimiter = "---CALL---";

        private static readonly IReadOnlyList<string> ManagedEnvironmentKeys = new[]
        {
            "PATH",
            ProcessCommandResolver.FfmpegPathEnvVar,
            "FAKE_FFMPEG_LOG",
            "FAKE_FFMPEG_STATE",
            "FAKE_FFMPEG_TOUCH_OUTPUT",
            "FAKE_FFMPEG_EXIT_CODE",
            "FAKE_FFMPEG_CREATE_PARTIAL",
            "FAKE_FFMPEG_FAIL_FIRST"
        };

        private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);

        public FakeFfmpegEnvironment(bool touchOutputOnSuccess, int exitCode, bool createPartialOutput, bool failFirstAttemptOnly)
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "mediaplayer-controls-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            string binDirectory = Path.Combine(RootDirectory, "bin");

            Directory.CreateDirectory(binDirectory);

            LogPath = Path.Combine(RootDirectory, "ffmpeg.log");
            StatePath = Path.Combine(RootDirectory, "ffmpeg.state");

            string executablePath = Path.Combine(binDirectory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.cmd" : "ffmpeg");
            string script = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? BuildWindowsScript() : BuildUnixScript();
            File.WriteAllText(executablePath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(executablePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            foreach (string key in ManagedEnvironmentKeys)
            {
                _originalEnvironment[key] = Environment.GetEnvironmentVariable(key);
            }

            string originalPath = _originalEnvironment["PATH"] ?? string.Empty;
            Environment.SetEnvironmentVariable("PATH", string.IsNullOrEmpty(originalPath)
                ? binDirectory
                : string.Concat(binDirectory, Path.PathSeparator, originalPath));
            Environment.SetEnvironmentVariable(ProcessCommandResolver.FfmpegPathEnvVar, executablePath);
            Environment.SetEnvironmentVariable("FAKE_FFMPEG_LOG", LogPath);
            Environment.SetEnvironmentVariable("FAKE_FFMPEG_STATE", StatePath);
            Environment.SetEnvironmentVariable("FAKE_FFMPEG_TOUCH_OUTPUT", touchOutputOnSuccess ? "1" : "0");
            Environment.SetEnvironmentVariable("FAKE_FFMPEG_EXIT_CODE", exitCode.ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable("FAKE_FFMPEG_CREATE_PARTIAL", createPartialOutput ? "1" : "0");
            Environment.SetEnvironmentVariable("FAKE_FFMPEG_FAIL_FIRST", failFirstAttemptOnly ? "1" : "0");
        }

        public string RootDirectory { get; }

        public string LogPath { get; }

        public string StatePath { get; }

        public IReadOnlyList<IReadOnlyList<string>> ReadCalls()
        {
            if (!File.Exists(LogPath))
            {
                return Array.Empty<IReadOnlyList<string>>();
            }

            List<IReadOnlyList<string>> calls = new();
            List<string>? currentCall = null;
            string[] lines = File.ReadAllLines(LogPath);

            foreach (string line in lines)
            {
                if (string.Equals(line, CallDelimiter, StringComparison.Ordinal))
                {
                    currentCall = new List<string>();
                    calls.Add(currentCall);
                    continue;
                }

                if (currentCall is not null)
                {
                    currentCall.Add(line);
                }
            }

            return calls;
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, string?> kvp in _originalEnvironment)
            {
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
            }

            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup of temporary fake ffmpeg workspace.
            }
        }

        private static string BuildUnixScript()
        {
            return "#!/bin/sh\n" +
                "set -eu\n" +
                "log=\"${FAKE_FFMPEG_LOG}\"\n" +
                "state=\"${FAKE_FFMPEG_STATE}\"\n" +
                "printf '%s\\n' '---CALL---' >> \"$log\"\n" +
                "last=''\n" +
                "for arg in \"$@\"; do\n" +
                "  last=\"$arg\"\n" +
                "  printf '%s\\n' \"$arg\" >> \"$log\"\n" +
                "done\n" +
                "exit_code=\"${FAKE_FFMPEG_EXIT_CODE:-0}\"\n" +
                "if [ \"${FAKE_FFMPEG_FAIL_FIRST:-0}\" = '1' ]; then\n" +
                "  count=0\n" +
                "  if [ -f \"$state\" ]; then\n" +
                "    count=$(cat \"$state\")\n" +
                "  fi\n" +
                "  count=$((count + 1))\n" +
                "  printf '%s' \"$count\" > \"$state\"\n" +
                "  if [ \"$count\" -eq 1 ]; then\n" +
                "    exit_code=1\n" +
                "  else\n" +
                "    exit_code=0\n" +
                "  fi\n" +
                "fi\n" +
                "if [ \"${FAKE_FFMPEG_CREATE_PARTIAL:-0}\" = '1' ] && [ -n \"$last\" ]; then\n" +
                "  printf 'partial' > \"$last\"\n" +
                "fi\n" +
                "if [ \"${FAKE_FFMPEG_TOUCH_OUTPUT:-0}\" = '1' ] && [ \"$exit_code\" -eq 0 ] && [ -n \"$last\" ]; then\n" +
                "  : > \"$last\"\n" +
                "fi\n" +
                "exit \"$exit_code\"\n";
        }

        private static string BuildWindowsScript()
        {
            return "@echo off\r\n" +
                "setlocal EnableDelayedExpansion\r\n" +
                "set \"log=%FAKE_FFMPEG_LOG%\"\r\n" +
                "set \"state=%FAKE_FFMPEG_STATE%\"\r\n" +
                "if not exist \"%log%\" type nul > \"%log%\"\r\n" +
                ">>\"%log%\" echo ---CALL---\r\n" +
                "set \"last=\"\r\n" +
                ":loop\r\n" +
                "if \"%~1\"==\"\" goto afterargs\r\n" +
                "set \"last=%~1\"\r\n" +
                ">>\"%log%\" echo %~1\r\n" +
                "shift\r\n" +
                "goto loop\r\n" +
                ":afterargs\r\n" +
                "set \"exit_code=%FAKE_FFMPEG_EXIT_CODE%\"\r\n" +
                "if \"%exit_code%\"==\"\" set \"exit_code=0\"\r\n" +
                "if \"%FAKE_FFMPEG_FAIL_FIRST%\"==\"1\" (\r\n" +
                "  set \"count=0\"\r\n" +
                "  if exist \"%state%\" set /p count=<\"%state%\"\r\n" +
                "  set /a count=count+1\r\n" +
                "  >\"%state%\" echo !count!\r\n" +
                "  if !count! EQU 1 (\r\n" +
                "    set \"exit_code=1\"\r\n" +
                "  ) else (\r\n" +
                "    set \"exit_code=0\"\r\n" +
                "  )\r\n" +
                ")\r\n" +
                "if \"%FAKE_FFMPEG_CREATE_PARTIAL%\"==\"1\" if not \"%last%\"==\"\" (\r\n" +
                "  >\"%last%\" echo partial\r\n" +
                ")\r\n" +
                "if \"%FAKE_FFMPEG_TOUCH_OUTPUT%\"==\"1\" if not \"%last%\"==\"\" if \"%exit_code%\"==\"0\" (\r\n" +
                "  type nul > \"%last%\"\r\n" +
                ")\r\n" +
                "exit /b %exit_code%\r\n";
        }
    }
}
