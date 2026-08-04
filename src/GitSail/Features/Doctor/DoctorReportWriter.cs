using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using GitSail.Git.Execution;

namespace GitSail.Features.Doctor;

/// <summary>
/// Writes human-readable and stable JSON diagnostic reports.
/// </summary>
internal static class DoctorReportWriter
{
    /// <summary>
    /// Writes a diagnostic report without mutating the host or repository.
    /// </summary>
    /// <param name="json">Whether to emit stable JSON instead of text.</param>
    /// <param name="git">The resolved Git installation, when available.</param>
    /// <param name="gitError">The actionable Git discovery error, when unavailable.</param>
    internal static void Write(bool json, GitInstallation? git, string? gitError)
    {
        if (json)
        {
            WriteJson(git, gitError);
            return;
        }

        Console.Out.WriteLine($"Product: {BuildInformation.DisplayVersion}");
        Console.Out.WriteLine($"Runtime identifier: {RuntimeInformation.RuntimeIdentifier}");
        Console.Out.WriteLine($"Operating system: {RuntimeInformation.OSDescription}");
        Console.Out.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        Console.Out.WriteLine($"Native AOT: {!RuntimeFeature.IsDynamicCodeSupported}");
        Console.Out.WriteLine($"Terminal: {GetTerminalDescription()}");
        Console.Out.WriteLine(git is null ? $"Git: unavailable ({gitError})" : $"Git: {git.Version} ({git.Executable.Path})");
    }

    private static void WriteJson(GitInstallation? git, string? gitError)
    {
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("product", BuildInformation.ProductName);
        writer.WriteString("version", BuildInformation.Version);
        writer.WriteString("runtimeIdentifier", RuntimeInformation.RuntimeIdentifier);
        writer.WriteString("operatingSystem", RuntimeInformation.OSDescription);
        writer.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
        writer.WriteBoolean("nativeAot", !RuntimeFeature.IsDynamicCodeSupported);
        writer.WriteString("terminal", GetTerminalDescription());
        writer.WriteStartObject("git");
        writer.WriteBoolean("available", git is not null);
        if (git is not null)
        {
            writer.WriteString("path", git.Executable.Path);
            writer.WriteString("version", git.Version.ToString());
        }
        else
        {
            writer.WriteString("error", gitError);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        Console.Out.WriteLine();
    }

    private static string GetTerminalDescription()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return "redirected";
        }

        return $"{Console.WindowWidth}x{Console.WindowHeight}";
    }
}
