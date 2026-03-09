using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MediaPlayer.Controls.Tests;

internal static class TestProcessShimLocator
{
    internal const string DotnetLogEnvVar = "MEDIAPLAYER_TEST_DOTNET_LOG";
    private const string HelperProjectName = "MediaPlayer.TestShim";

    public static string CreateAliasExecutable(string aliasName, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aliasName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        string helperOutputDirectory = ResolveHelperOutputDirectory();
        Directory.CreateDirectory(destinationDirectory);

        foreach (string sourceFilePath in Directory.EnumerateFiles(helperOutputDirectory))
        {
            string destinationFilePath = Path.Combine(destinationDirectory, Path.GetFileName(sourceFilePath));
            File.Copy(sourceFilePath, destinationFilePath, overwrite: true);
        }

        string executableExtension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;
        string sourceExecutablePath = Path.Combine(destinationDirectory, HelperProjectName + executableExtension);
        if (!File.Exists(sourceExecutablePath))
        {
            throw new FileNotFoundException("The test shim executable was not found in the copied output directory.", sourceExecutablePath);
        }

        string aliasExecutablePath = Path.Combine(destinationDirectory, aliasName + executableExtension);
        File.Copy(sourceExecutablePath, aliasExecutablePath, overwrite: true);

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode executableMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

            File.SetUnixFileMode(sourceExecutablePath, executableMode);
            File.SetUnixFileMode(aliasExecutablePath, executableMode);
        }

        return aliasExecutablePath;
    }

    private static string ResolveHelperOutputDirectory()
    {
        string baseDirectoryPath = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        DirectoryInfo targetFrameworkDirectory = new(baseDirectoryPath);
        DirectoryInfo? configurationDirectory = targetFrameworkDirectory?.Parent;
        if (targetFrameworkDirectory is null || configurationDirectory is null)
        {
            throw new DirectoryNotFoundException("Unable to resolve the current test output layout.");
        }

        string helperOutputDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            HelperProjectName,
            "bin",
            configurationDirectory.Name,
            targetFrameworkDirectory.Name));

        if (!Directory.Exists(helperOutputDirectory))
        {
            throw new DirectoryNotFoundException(FormattableString.Invariant(
                $"The test shim output directory '{helperOutputDirectory}' does not exist. Build the solution before running tests with --no-build."));
        }

        return helperOutputDirectory;
    }
}
