using System;
using System.IO;
using System.Runtime.InteropServices;
using MediaPlayer.Controls;
using MediaPlayer.Controls.Workflows;
using MediaPlayer.Native.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MediaPlayer.Controls.Tests.Workflows;

public sealed class MediaPlayerWorkflowServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMediaPlayerWorkflows_RegistersExpectedDefaultServiceForPlatform()
    {
        ServiceCollection services = new();

        services.AddMediaPlayerWorkflows();
        using ServiceProvider provider = services.BuildServiceProvider();

        IMediaWorkflowService workflow = provider.GetRequiredService<IMediaWorkflowService>();
        IMediaWorkflowProviderDiagnostics diagnostics = provider.GetRequiredService<IMediaWorkflowProviderDiagnostics>();
        Assert.IsType<InteropMediaWorkflowService>(workflow);
        Assert.Equal(MediaPlayerNativeProviderMode.AutoPreferInterop, diagnostics.Current.ConfiguredMode);
        Assert.Equal(MediaPlayerNativeProviderKind.Interop, diagnostics.Current.ActiveProvider);
        Assert.Equal(string.Empty, diagnostics.Current.FallbackReason);
    }

    [Fact]
    public void AddMediaPlayerWorkflows_WhenNativeDisabled_RegistersFfmpegWorkflow()
    {
        ServiceCollection services = new();

        services.AddMediaPlayerWorkflows(options => options.PreferNativePlatformServices = false);
        using ServiceProvider provider = services.BuildServiceProvider();

        IMediaWorkflowService workflow = provider.GetRequiredService<IMediaWorkflowService>();
        IMediaWorkflowProviderDiagnostics diagnostics = provider.GetRequiredService<IMediaWorkflowProviderDiagnostics>();
        Assert.IsType<FfmpegMediaWorkflowService>(workflow);
        Assert.Equal(MediaPlayerNativeProviderMode.AutoPreferInterop, diagnostics.Current.ConfiguredMode);
        Assert.Equal(MediaPlayerNativeProviderKind.FfmpegFallback, diagnostics.Current.ActiveProvider);
        Assert.Contains("disabled", diagnostics.Current.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddMediaPlayerWorkflows_InteropOnly_UsesInteropWithDiagnostics()
    {
        ServiceCollection services = new();

        services.AddMediaPlayerWorkflows(options => options.NativeProviderMode = MediaPlayerNativeProviderMode.InteropOnly);
        using ServiceProvider provider = services.BuildServiceProvider();

        IMediaWorkflowService workflow = provider.GetRequiredService<IMediaWorkflowService>();
        IMediaWorkflowProviderDiagnostics diagnostics = provider.GetRequiredService<IMediaWorkflowProviderDiagnostics>();
        Assert.IsType<InteropMediaWorkflowService>(workflow);
        Assert.Equal(MediaPlayerNativeProviderMode.InteropOnly, diagnostics.Current.ConfiguredMode);
        Assert.Equal(MediaPlayerNativeProviderKind.Interop, diagnostics.Current.ActiveProvider);
        Assert.Equal(string.Empty, diagnostics.Current.FallbackReason);
    }

    [Fact]
    public void AddMediaPlayerWorkflows_NativeBindingsOnly_UsesInteropFallbackWithDiagnostics()
    {
        ServiceCollection services = new();

        services.AddMediaPlayerWorkflows(options => options.NativeProviderMode = MediaPlayerNativeProviderMode.NativeBindingsOnly);
        using ServiceProvider provider = services.BuildServiceProvider();

        IMediaWorkflowService workflow = provider.GetRequiredService<IMediaWorkflowService>();
        IMediaWorkflowProviderDiagnostics diagnostics = provider.GetRequiredService<IMediaWorkflowProviderDiagnostics>();
        Assert.IsType<InteropMediaWorkflowService>(workflow);
        Assert.Equal(MediaPlayerNativeProviderMode.NativeBindingsOnly, diagnostics.Current.ConfiguredMode);
        Assert.Equal(MediaPlayerNativeProviderKind.Interop, diagnostics.Current.ActiveProvider);
        Assert.Contains("NativeBindingsOnly", diagnostics.Current.FallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public void AddMediaPlayerWorkflows_AutoPreferBindings_UsesExpectedServicePerPlatform()
    {
        ServiceCollection services = new();

        services.AddMediaPlayerWorkflows(options => options.NativeProviderMode = MediaPlayerNativeProviderMode.AutoPreferBindings);
        using ServiceProvider provider = services.BuildServiceProvider();

        IMediaWorkflowService workflow = provider.GetRequiredService<IMediaWorkflowService>();
        IMediaWorkflowProviderDiagnostics diagnostics = provider.GetRequiredService<IMediaWorkflowProviderDiagnostics>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.IsType<MacOsNativeMediaWorkflowService>(workflow);
            Assert.Equal(MediaPlayerNativeProviderKind.LegacyHelper, diagnostics.Current.ActiveProvider);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.IsType<WindowsNativeMediaWorkflowService>(workflow);
            Assert.Equal(MediaPlayerNativeProviderKind.LegacyHelper, diagnostics.Current.ActiveProvider);
            return;
        }

        Assert.IsType<InteropMediaWorkflowService>(workflow);
        Assert.Equal(MediaPlayerNativeProviderKind.Interop, diagnostics.Current.ActiveProvider);
        Assert.Contains("not supported", diagnostics.Current.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddMediaPlayerWorkflows_LegacyHelpers_UsesExpectedServicePerPlatform()
    {
        ServiceCollection services = new();

        services.AddMediaPlayerWorkflows(options => options.NativeProviderMode = MediaPlayerNativeProviderMode.LegacyHelpers);
        using ServiceProvider provider = services.BuildServiceProvider();

        IMediaWorkflowService workflow = provider.GetRequiredService<IMediaWorkflowService>();
        IMediaWorkflowProviderDiagnostics diagnostics = provider.GetRequiredService<IMediaWorkflowProviderDiagnostics>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.IsType<MacOsNativeMediaWorkflowService>(workflow);
            Assert.Equal(MediaPlayerNativeProviderKind.LegacyHelper, diagnostics.Current.ActiveProvider);
            Assert.Equal(string.Empty, diagnostics.Current.FallbackReason);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.IsType<WindowsNativeMediaWorkflowService>(workflow);
            Assert.Equal(MediaPlayerNativeProviderKind.LegacyHelper, diagnostics.Current.ActiveProvider);
            Assert.Equal(string.Empty, diagnostics.Current.FallbackReason);
            return;
        }

        Assert.IsType<InteropMediaWorkflowService>(workflow);
        Assert.Equal(MediaPlayerNativeProviderKind.Interop, diagnostics.Current.ActiveProvider);
        Assert.Contains("not available", diagnostics.Current.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddMediaPlayerWorkflows_WithFactory_UsesFactoryImplementation()
    {
        ServiceCollection services = new();
        FakeWorkflowService fake = new();

        services.AddMediaPlayerWorkflows(_ => fake);
        using ServiceProvider provider = services.BuildServiceProvider();

        IMediaWorkflowService workflow = provider.GetRequiredService<IMediaWorkflowService>();
        IMediaWorkflowProviderDiagnostics diagnostics = provider.GetRequiredService<IMediaWorkflowProviderDiagnostics>();
        Assert.Same(fake, workflow);
        Assert.Equal(MediaPlayerNativeProviderKind.Unknown, diagnostics.Current.ActiveProvider);
        Assert.Contains("unavailable", diagnostics.Current.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async System.Threading.Tasks.Task WindowsNativeMediaWorkflowService_UsesDotnetHost_ForDllHelperPath_AndPassesQualityProfile()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using DotnetShimHarness fake = new();
        string helperRoot = Path.Combine(fake.RootDirectory, "helper");
        Directory.CreateDirectory(helperRoot);

        string helperDllPath = Path.Combine(helperRoot, "fake-helper.dll");
        await File.WriteAllTextAsync(helperDllPath, "fake dll payload");
        string helperHostPath = Path.Combine(helperRoot, "dotnet.cmd");
        File.WriteAllText(helperHostPath, BuildDotnetShimBatch(fake.LogPath));

        string? originalDotnetHost = Environment.GetEnvironmentVariable(ProcessCommandResolver.DotnetHostEnvVar);
        Environment.SetEnvironmentVariable(ProcessCommandResolver.DotnetHostEnvVar, helperHostPath);
        try
        {
            IMediaWorkflowService fallback = new FfmpegMediaWorkflowService();
            WindowsNativeMediaWorkflowService service = new(fallback, helperDllPath);

            string sourcePath = Path.Combine(fake.RootDirectory, "input.mp4");
            await File.WriteAllTextAsync(sourcePath, "source");
            string outputPath = Path.Combine(fake.RootDirectory, "output.mp4");

            MediaWorkflowResult result = await service.ExportAsync(
                new Uri(sourcePath),
                outputPath,
                MediaExportPreset.Video1080p,
                MediaWorkflowQualityProfile.Quality);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(File.Exists(outputPath));

            IReadOnlyList<IReadOnlyList<string>> calls = fake.ReadCalls();
            IReadOnlyList<string> firstCall = Assert.Single(calls);
            Assert.Equal(helperDllPath, firstCall[0]);
            Assert.Equal("export", firstCall[1]);
            Assert.Contains("--preset", firstCall);
            Assert.Contains(nameof(MediaExportPreset.Video1080p), firstCall);
            Assert.Contains("--qualityProfile", firstCall);
            Assert.Contains(nameof(MediaWorkflowQualityProfile.Quality), firstCall);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProcessCommandResolver.DotnetHostEnvVar, originalDotnetHost);
        }
    }

    private sealed class DotnetShimHarness : IDisposable
    {
        private const string CallDelimiter = "---CALL---";

        public DotnetShimHarness()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "mediaplayer-controls-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootDirectory);
            LogPath = Path.Combine(RootDirectory, "dotnet-shim.log");
        }

        public string RootDirectory { get; }

        public string LogPath { get; }

        public IReadOnlyList<IReadOnlyList<string>> ReadCalls()
        {
            if (!File.Exists(LogPath))
            {
                return Array.Empty<IReadOnlyList<string>>();
            }

            List<IReadOnlyList<string>> calls = new();
            List<string>? currentCall = null;
            foreach (string line in File.ReadAllLines(LogPath))
            {
                if (string.Equals(line, CallDelimiter, StringComparison.Ordinal))
                {
                    currentCall = new List<string>();
                    calls.Add(currentCall);
                    continue;
                }

                currentCall?.Add(line);
            }

            return calls;
        }

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
                // Best effort temp workspace cleanup.
            }
        }
    }

    private static string BuildDotnetShimBatch(string logPath)
    {
        return "@echo off\r\n" +
               "setlocal EnableDelayedExpansion\r\n" +
               "set \"log=" + logPath.Replace("\"", "\"\"") + "\"\r\n" +
               "set \"outputPath=\"\r\n" +
               "if not exist \"%log%\" type nul > \"%log%\"\r\n" +
               ">>\"%log%\" echo ---CALL---\r\n" +
               ":loop\r\n" +
               "if \"%~1\"==\"\" goto end\r\n" +
               ">>\"%log%\" echo %~1\r\n" +
               "if /I \"%~1\"==\"--output\" (\r\n" +
                "  shift\r\n" +
               "  if not \"%~1\"==\"\" (\r\n" +
               "    set \"outputPath=%~1\"\r\n" +
               "    >>\"%log%\" echo %~1\r\n" +
               "  )\r\n" +
               "  shift\r\n" +
               "  goto loop\r\n" +
               ")\r\n" +
               "shift\r\n" +
               "goto loop\r\n" +
               ":end\r\n" +
               "if not \"%outputPath%\"==\"\" type nul > \"%outputPath%\"\r\n" +
               "exit /b 0\r\n";
    }

    private sealed class FakeWorkflowService : IMediaWorkflowService
    {
        public string GetExportPresetDisplayName(MediaExportPreset preset) => string.Empty;

        public string GetRecordingPresetDisplayName(MediaRecordingPreset preset) => string.Empty;

        public string GetQualityProfileDisplayName(MediaWorkflowQualityProfile profile) => string.Empty;

        public string GetSuggestedExportFileName(Uri source, MediaExportPreset preset) => string.Empty;

        public string GetSuggestedRecordingFileName(MediaRecordingPreset preset, DateTime timestamp) => string.Empty;

        public string BuildSiblingOutputPath(string outputPath, string siblingSuffix) => string.Empty;

        public System.Threading.Tasks.Task<MediaWorkflowResult> TrimAsync(
            Uri source,
            TimeSpan startTime,
            TimeSpan endTime,
            string outputPath,
            System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.FromResult(MediaWorkflowResult.Ok());

        public System.Threading.Tasks.Task<MediaWorkflowResult> SplitAsync(
            Uri source,
            TimeSpan splitTime,
            TimeSpan duration,
            string partOnePath,
            string partTwoPath,
            System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.FromResult(MediaWorkflowResult.Ok());

        public System.Threading.Tasks.Task<MediaWorkflowResult> CombineAsync(
            System.Collections.Generic.IReadOnlyList<string> inputPaths,
            string outputPath,
            System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.FromResult(MediaWorkflowResult.Ok());

        public System.Threading.Tasks.Task<MediaWorkflowResult> RemoveAudioAsync(
            Uri source,
            string outputPath,
            System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.FromResult(MediaWorkflowResult.Ok());

        public System.Threading.Tasks.Task<MediaWorkflowResult> RemoveVideoAsync(
            Uri source,
            string outputPath,
            System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.FromResult(MediaWorkflowResult.Ok());

        public System.Threading.Tasks.Task<MediaWorkflowResult> TransformAsync(
            Uri source,
            string outputPath,
            MediaVideoTransform transform,
            System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.FromResult(MediaWorkflowResult.Ok());

        public System.Threading.Tasks.Task<MediaWorkflowResult> ExportAsync(
            Uri source,
            string outputPath,
            MediaExportPreset preset,
            MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
            System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.FromResult(MediaWorkflowResult.Ok());

        public System.Threading.Tasks.Task<MediaWorkflowResult> RecordAsync(
            MediaRecordingPreset preset,
            string outputPath,
            TimeSpan duration,
            MediaWorkflowQualityProfile qualityProfile = MediaWorkflowQualityProfile.Balanced,
            System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.FromResult(MediaWorkflowResult.Ok());
    }
}
