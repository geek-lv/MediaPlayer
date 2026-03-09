using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MediaPlayer.Controls;

internal static class ProcessCommandResolver
{
    internal const string DotnetHostEnvVar = "MEDIAPLAYER_DOTNET_HOST";
    internal const string FfmpegPathEnvVar = "MEDIAPLAYER_FFMPEG_PATH";
    internal const string FfprobePathEnvVar = "MEDIAPLAYER_FFPROBE_PATH";
    internal const string FfplayPathEnvVar = "MEDIAPLAYER_FFPLAY_PATH";

    public static string ResolveDotnetHost()
    {
        return ResolveToolPath(DotnetHostEnvVar, "dotnet");
    }

    public static string ResolveFfmpegExecutable()
    {
        return ResolveToolPath(FfmpegPathEnvVar, "ffmpeg");
    }

    public static string ResolveFfprobeExecutable()
    {
        return ResolveSiblingToolPath(FfprobePathEnvVar, "ffprobe");
    }

    public static string ResolveFfplayExecutable()
    {
        return ResolveSiblingToolPath(FfplayPathEnvVar, "ffplay");
    }

    public static void ConfigureTool(ProcessStartInfo startInfo, string toolPathOrCommand)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPathOrCommand);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && HasBatchExtension(toolPathOrCommand))
        {
            startInfo.FileName = ResolveWindowsCommandInterpreter();
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(toolPathOrCommand);
            return;
        }

        startInfo.FileName = toolPathOrCommand;
    }

    private static string ResolveToolPath(string envVarName, string defaultCommand)
    {
        var configured = NormalizeConfiguredToolPath(Environment.GetEnvironmentVariable(envVarName));
        return string.IsNullOrWhiteSpace(configured) ? defaultCommand : configured;
    }

    private static string ResolveSiblingToolPath(string envVarName, string toolBaseName)
    {
        var configured = NormalizeConfiguredToolPath(Environment.GetEnvironmentVariable(envVarName));
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = NormalizeConfiguredToolPath(Environment.GetEnvironmentVariable(FfmpegPathEnvVar));
            if (string.IsNullOrWhiteSpace(configured) || !LooksLikePath(configured))
            {
                return toolBaseName;
            }

            var directory = Path.GetDirectoryName(configured);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return toolBaseName;
            }

            return Path.Combine(directory, toolBaseName + Path.GetExtension(configured));
        }

        return configured;
    }

    private static string? NormalizeConfiguredToolPath(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        configured = configured.Trim();
        if (configured.Length > 1 && configured[0] == '"' && configured[^1] == '"')
        {
            configured = configured[1..^1];
        }

        return configured;
    }

    private static string ResolveWindowsCommandInterpreter()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        return string.IsNullOrWhiteSpace(comSpec) ? "cmd.exe" : comSpec;
    }

    private static bool HasBatchExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePath(string value)
    {
        return Path.IsPathRooted(value)
               || value.IndexOf(Path.DirectorySeparatorChar) >= 0
               || value.IndexOf(Path.AltDirectorySeparatorChar) >= 0;
    }
}
