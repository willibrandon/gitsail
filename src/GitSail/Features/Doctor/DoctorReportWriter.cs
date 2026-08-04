using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

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
    internal static void Write(bool json)
    {
        if (json)
        {
            WriteJson();
            return;
        }

        Console.Out.WriteLine($"Product: {BuildInformation.DisplayVersion}");
        Console.Out.WriteLine($"Runtime identifier: {RuntimeInformation.RuntimeIdentifier}");
        Console.Out.WriteLine($"Operating system: {RuntimeInformation.OSDescription}");
        Console.Out.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        Console.Out.WriteLine($"Native AOT: {!RuntimeFeature.IsDynamicCodeSupported}");
        Console.Out.WriteLine($"Terminal: {GetTerminalDescription()}");
    }

    private static void WriteJson()
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
