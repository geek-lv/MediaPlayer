using System;
using MediaPlayer.Native.Abstractions;

namespace MediaPlayer.Controls.Tests.Native;

public sealed class MediaPlayerNativeProviderModeParserTests
{
    [Theory]
    [InlineData("legacy", MediaPlayerNativeProviderMode.LegacyHelpers)]
    [InlineData("interop-only", MediaPlayerNativeProviderMode.InteropOnly)]
    [InlineData("native_bindings", MediaPlayerNativeProviderMode.NativeBindingsOnly)]
    [InlineData("auto", MediaPlayerNativeProviderMode.AutoPreferInterop)]
    [InlineData("auto-prefer-bindings", MediaPlayerNativeProviderMode.AutoPreferBindings)]
    [InlineData("4", MediaPlayerNativeProviderMode.AutoPreferBindings)]
    public void TryParse_ReturnsExpectedMode_ForSupportedAliases(string raw, MediaPlayerNativeProviderMode expected)
    {
        var parsed = MediaPlayerNativeProviderModeParser.TryParse(raw, out var mode);

        Assert.True(parsed);
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("unknown-mode")]
    public void TryParse_ReturnsFalse_ForInvalidInputs(string? raw)
    {
        var parsed = MediaPlayerNativeProviderModeParser.TryParse(raw, out var mode);

        Assert.False(parsed);
        Assert.Equal(MediaPlayerNativeProviderMode.AutoPreferInterop, mode);
    }

    [Fact]
    public void FromEnvironment_UsesConfiguredMode()
    {
        var previous = Environment.GetEnvironmentVariable(MediaPlayerNativeEnvironment.ProviderModeVariableName);
        try
        {
            Environment.SetEnvironmentVariable(MediaPlayerNativeEnvironment.ProviderModeVariableName, "native-bindings");

            var options = MediaPlayerNativeOptions.FromEnvironment();

            Assert.Equal(MediaPlayerNativeProviderMode.NativeBindingsOnly, options.ProviderMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MediaPlayerNativeEnvironment.ProviderModeVariableName, previous);
        }
    }
}
