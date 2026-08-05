using System.ComponentModel;
using System.Diagnostics;

namespace GitSail.ToolLauncher;

/// <summary>
/// Runs the Native AOT application payload from its installed package directory.
/// </summary>
internal static class ToolLauncher
{
    /// <summary>
    /// Starts the Native AOT application with the supplied command-line arguments.
    /// </summary>
    /// <param name="arguments">The arguments to forward without modification.</param>
    /// <returns>The Native AOT application's exit code.</returns>
    internal static int Run(string[] arguments)
    {
        var executableName = OperatingSystem.IsWindows() ? "git-tui.exe" : "git-tui";
        var executablePath = Path.Combine(AppContext.BaseDirectory, executableName);
        if (!File.Exists(executablePath))
        {
            Console.Error.WriteLine($"GitSail's Native AOT application is missing: {executablePath}");
            return 1;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Console.Error.WriteLine("GitSail's Native AOT application could not be started.");
                return 1;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"GitSail's Native AOT application could not be started: {exception.Message}");
            return 1;
        }
    }
}
