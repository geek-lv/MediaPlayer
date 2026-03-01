using System.Reflection;
using MediaPlayer.Controls.Workflows;
using Xunit;

namespace MediaPlayer.Controls.Tests.Workflows;

public sealed class MacOsNativeMediaWorkflowServiceTests
{
    [Fact]
    public void TryMapExportPreset_AppliesQualityFlags_ForVideoPresets()
    {
        AssertExportMapping(
            MediaExportPreset.Video1080p,
            MediaWorkflowQualityProfile.Speed,
            "Preset1920x1080",
            expectedMultiPass: false,
            expectedDisableFastStart: true);

        AssertExportMapping(
            MediaExportPreset.Video1080p,
            MediaWorkflowQualityProfile.Balanced,
            "Preset1920x1080",
            expectedMultiPass: false,
            expectedDisableFastStart: false);

        AssertExportMapping(
            MediaExportPreset.Video1080p,
            MediaWorkflowQualityProfile.Quality,
            "Preset1920x1080",
            expectedMultiPass: true,
            expectedDisableFastStart: false);
    }

    [Fact]
    public void TryMapExportPreset_LeavesAudioOnlyPresetStable()
    {
        AssertExportMapping(
            MediaExportPreset.AudioOnly,
            MediaWorkflowQualityProfile.Speed,
            "PresetAppleM4A",
            expectedMultiPass: false,
            expectedDisableFastStart: false);
    }

    [Fact]
    public void TryMapRecordingConversionPreset_UsesExpectedNativePresets()
    {
        AssertRecordingMapping(MediaWorkflowQualityProfile.Speed, "PresetMediumQuality", expectedMultiPass: false, expectedDisableFastStart: true);
        AssertRecordingMapping(MediaWorkflowQualityProfile.Balanced, "PresetHighestQuality", expectedMultiPass: false, expectedDisableFastStart: false);
        AssertRecordingMapping(MediaWorkflowQualityProfile.Quality, "PresetHighestQuality", expectedMultiPass: true, expectedDisableFastStart: false);
    }

    private static void AssertExportMapping(
        MediaExportPreset preset,
        MediaWorkflowQualityProfile profile,
        string expectedPreset,
        bool expectedMultiPass,
        bool expectedDisableFastStart)
    {
        var method = typeof(MacOsNativeMediaWorkflowService).GetMethod(
            "TryMapExportPreset",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { preset, profile, null, null, null };
        var result = (bool)method!.Invoke(null, args)!;
        Assert.True(result);
        Assert.Equal(expectedPreset, (string)args[2]!);
        Assert.Equal(expectedMultiPass, (bool)args[3]!);
        Assert.Equal(expectedDisableFastStart, (bool)args[4]!);
    }

    private static void AssertRecordingMapping(
        MediaWorkflowQualityProfile profile,
        string expectedPreset,
        bool expectedMultiPass,
        bool expectedDisableFastStart)
    {
        var method = typeof(MacOsNativeMediaWorkflowService).GetMethod(
            "TryMapRecordingConversionPreset",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { profile, null, null, null };
        var result = (bool)method!.Invoke(null, args)!;
        Assert.True(result);
        Assert.Equal(expectedPreset, (string)args[1]!);
        Assert.Equal(expectedMultiPass, (bool)args[2]!);
        Assert.Equal(expectedDisableFastStart, (bool)args[3]!);
    }
}
