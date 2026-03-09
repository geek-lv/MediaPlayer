using System.Globalization;
using System.Text;

string executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? AppDomain.CurrentDomain.FriendlyName);

return executableName.ToLowerInvariant() switch
{
    "ffmpeg" => RunFakeFfmpeg(args),
    "dotnet" => RunFakeDotnet(args),
    _ => 1
};

static int RunFakeFfmpeg(string[] args)
{
    string logPath = GetRequiredEnvironmentVariable("FAKE_FFMPEG_LOG");
    string statePath = GetRequiredEnvironmentVariable("FAKE_FFMPEG_STATE");
    AppendCall(logPath, args);

    string? outputPath = args.Length > 0 ? args[^1] : null;
    int exitCode = ParseInt32(Environment.GetEnvironmentVariable("FAKE_FFMPEG_EXIT_CODE"), 0);

    if (IsEnabled("FAKE_FFMPEG_FAIL_FIRST"))
    {
        int invocationCount = 0;
        if (File.Exists(statePath))
        {
            _ = int.TryParse(File.ReadAllText(statePath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out invocationCount);
        }

        invocationCount++;
        File.WriteAllText(statePath, invocationCount.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        exitCode = invocationCount == 1 ? 1 : 0;
    }

    if (!string.IsNullOrWhiteSpace(outputPath) && IsEnabled("FAKE_FFMPEG_CREATE_PARTIAL"))
    {
        EnsureParentDirectory(outputPath);
        File.WriteAllText(outputPath, "partial", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    if (!string.IsNullOrWhiteSpace(outputPath) && exitCode == 0 && IsEnabled("FAKE_FFMPEG_TOUCH_OUTPUT"))
    {
        EnsureParentDirectory(outputPath);
        using FileStream stream = File.Create(outputPath);
        stream.Flush();
    }

    return exitCode;
}

static int RunFakeDotnet(string[] args)
{
    string logPath = GetRequiredEnvironmentVariable("MEDIAPLAYER_TEST_DOTNET_LOG");
    AppendCall(logPath, args);

    string? outputPath = null;
    for (int index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], "--output", StringComparison.Ordinal))
        {
            outputPath = args[index + 1];
            break;
        }
    }

    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        EnsureParentDirectory(outputPath);
        using FileStream stream = File.Create(outputPath);
        stream.Flush();
    }

    return 0;
}

static void AppendCall(string logPath, IReadOnlyList<string> args)
{
    EnsureParentDirectory(logPath);
    using StreamWriter writer = new(logPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    writer.WriteLine("---CALL---");
    for (int index = 0; index < args.Count; index++)
    {
        writer.WriteLine(args[index]);
    }
}

static string GetRequiredEnvironmentVariable(string name)
{
    string? value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException(FormattableString.Invariant($"Environment variable '{name}' was not set."));
    }

    return value;
}

static int ParseInt32(string? value, int defaultValue)
{
    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
        ? parsed
        : defaultValue;
}

static bool IsEnabled(string environmentVariableName)
{
    return string.Equals(Environment.GetEnvironmentVariable(environmentVariableName), "1", StringComparison.Ordinal);
}

static void EnsureParentDirectory(string path)
{
    string? parentDirectory = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(parentDirectory))
    {
        Directory.CreateDirectory(parentDirectory);
    }
}
