using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MediaPlayer.Controls;

internal static class ProcessCommandResolver
{
    internal const string DotnetHostEnvVar = "MEDIAPLAYER_DOTNET_HOST";
    internal const string FfmpegPathEnvVar = "MEDIAPLAYER_FFMPEG_PATH";

    public static string ResolveDotnetHost()
    {
        return ResolveToolPath(DotnetHostEnvVar, "dotnet");
    }

    public static string ResolveFfmpegExecutable()
    {
        return ResolveToolPath(FfmpegPathEnvVar, "ffmpeg");
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
        var configured = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return defaultCommand;
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
}
