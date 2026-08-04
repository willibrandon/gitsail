using GitSail.Ui;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace GitSail.Features.Doctor;

/// <summary>
/// Writes human-readable and stable JSON diagnostic reports from one typed snapshot.
/// </summary>
internal static class DoctorReportWriter
{
    /// <summary>
    /// Writes one diagnostic report without reading additional process or repository state.
    /// </summary>
    /// <param name="json">Whether to emit stable JSON instead of text.</param>
    /// <param name="report">The complete typed diagnostic snapshot.</param>
    /// <param name="output">The invocation-owned output writer.</param>
    internal static void Write(bool json, DoctorReport report, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);
        if (json)
        {
            WriteJson(report, output);
            return;
        }

        output.WriteLine($"Product: {Sanitize(report.Product)} {Sanitize(report.Version)}");
        output.WriteLine($"Runtime identifier: {Sanitize(report.RuntimeIdentifier)}");
        output.WriteLine($"Operating system: {Sanitize(report.OperatingSystem)}");
        output.WriteLine($"Architecture: {Sanitize(report.Architecture)}");
        output.WriteLine($"Native AOT: {report.NativeAot}");
        output.WriteLine($"Command path: {Sanitize(report.CommandPath ?? "unavailable")}");
        output.WriteLine($"Installation scope: {Sanitize(report.InstallationScope)}");
        output.WriteLine($"Command PATH status: {Sanitize(report.CommandPathStatus)}");
        output.WriteLine($"Terminal: {Sanitize(report.Terminal.Description)}");
        output.WriteLine($"Terminal input redirected: {report.Terminal.InputRedirected}");
        output.WriteLine($"Terminal output redirected: {report.Terminal.OutputRedirected}");
        output.WriteLine($"Terminal color: {Sanitize(report.Terminal.Color)}");
        output.WriteLine($"Terminal input: {Sanitize(report.Terminal.Input)}");
        output.WriteLine($"Terminal mouse: {Sanitize(report.Terminal.Mouse)}");
        output.WriteLine($"Terminal Unicode: {Sanitize(report.Terminal.Unicode)}");
        output.WriteLine($"Terminal clipboard: {Sanitize(report.Terminal.Clipboard)}");
        output.WriteLine($"Culture: {Sanitize(report.Locale.Culture)}");
        output.WriteLine($"UI culture: {Sanitize(report.Locale.UICulture)}");
        output.WriteLine($"Globalization: {Sanitize(report.Locale.Globalization)}");
        output.WriteLine(report.Git.Available
            ? $"Git: {Sanitize(report.Git.Version!)} ({Sanitize(report.Git.Path!)})"
            : $"Git: unavailable ({Sanitize(report.Git.Error ?? "unknown error")})");
        output.WriteLine($"Git 2.36 baseline: {report.Git.MeetsMinimumVersion}");
        foreach (var capability in report.Git.Capabilities)
        {
            output.WriteLine(
                $"Git capability {Sanitize(capability.Name)}: {capability.Available} " +
                $"(requires {Sanitize(capability.Requirement)})");
        }

        output.WriteLine(report.Repository.Available
            ? $"Repository: {Sanitize(report.Repository.WorkTree ?? report.Repository.GitDirectory!)}"
            : $"Repository: unavailable ({Sanitize(report.Repository.Error ?? "not discovered")})");
        output.WriteLine($"Repository trust: {Sanitize(report.Repository.Trust)}");
        WriteTool(output, ".NET SDK", report.DotNetSdk);
        output.WriteLine(report.Ssh.Available
            ? $"SSH: {Sanitize(report.Ssh.Path!)}"
            : $"SSH: unavailable ({Sanitize(report.Ssh.Error ?? "unknown error")})");
        WritePath(output, report.Storage.Configuration);
        WritePath(output, report.Storage.Cache);
        WritePath(output, report.Storage.State);
        WritePath(output, report.Storage.Traces);
        if (report.Storage.Error is not null)
        {
            output.WriteLine($"Storage error: {Sanitize(report.Storage.Error)}");
        }

        output.WriteLine("Git configuration sources (values omitted):");
        foreach (var source in report.ConfigurationSources)
        {
            output.WriteLine($"  {Sanitize(source.Scope)}: {Sanitize(source.Origin)}");
        }

        if (report.ConfigurationSourcesTruncated)
        {
            output.WriteLine("  additional sources omitted at the diagnostic bound");
        }

        if (report.ConfigurationError is not null)
        {
            output.WriteLine($"Git configuration error: {Sanitize(report.ConfigurationError)}");
        }

        output.WriteLine($"Symbols: {Sanitize(report.SymbolLookup)}");
    }

    private static void WriteJson(DoctorReport report, TextWriter output)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("product", Sanitize(report.Product));
            writer.WriteString("version", Sanitize(report.Version));
            writer.WriteString("runtimeIdentifier", Sanitize(report.RuntimeIdentifier));
            writer.WriteString("operatingSystem", Sanitize(report.OperatingSystem));
            writer.WriteString("architecture", Sanitize(report.Architecture));
            writer.WriteBoolean("nativeAot", report.NativeAot);
            writer.WriteStartObject("command");
            WriteNullableString(writer, "path", report.CommandPath);
            writer.WriteString("installationScope", Sanitize(report.InstallationScope));
            writer.WriteString("pathStatus", Sanitize(report.CommandPathStatus));
            writer.WriteEndObject();
            writer.WriteString("terminal", Sanitize(report.Terminal.Description));
            writer.WriteStartObject("terminalCapabilities");
            writer.WriteBoolean("inputRedirected", report.Terminal.InputRedirected);
            writer.WriteBoolean("outputRedirected", report.Terminal.OutputRedirected);
            WriteNullableNumber(writer, "width", report.Terminal.Width);
            WriteNullableNumber(writer, "height", report.Terminal.Height);
            writer.WriteString("color", Sanitize(report.Terminal.Color));
            writer.WriteString("input", Sanitize(report.Terminal.Input));
            writer.WriteString("mouse", Sanitize(report.Terminal.Mouse));
            writer.WriteString("unicode", Sanitize(report.Terminal.Unicode));
            writer.WriteString("clipboard", Sanitize(report.Terminal.Clipboard));
            writer.WriteEndObject();
            writer.WriteStartObject("locale");
            writer.WriteString("culture", Sanitize(report.Locale.Culture));
            writer.WriteString("uiCulture", Sanitize(report.Locale.UICulture));
            writer.WriteString("inputEncoding", Sanitize(report.Locale.InputEncoding));
            writer.WriteString("outputEncoding", Sanitize(report.Locale.OutputEncoding));
            writer.WriteString("globalization", Sanitize(report.Locale.Globalization));
            writer.WriteEndObject();
            WriteGit(writer, report.Git);
            WriteRepository(writer, report.Repository);
            WriteTool(writer, report.DotNetSdk);
            WriteTool(writer, report.Ssh);
            WriteStorage(writer, report.Storage);
            writer.WriteStartArray("configurationSources");
            foreach (var source in report.ConfigurationSources)
            {
                writer.WriteStartObject();
                writer.WriteString("scope", Sanitize(source.Scope));
                writer.WriteString("origin", Sanitize(source.Origin));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("configurationSourcesTruncated", report.ConfigurationSourcesTruncated);
            WriteNullableString(writer, "configurationError", report.ConfigurationError);
            writer.WriteString("symbolLookup", Sanitize(report.SymbolLookup));
            writer.WriteEndObject();
        }

        output.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void WriteGit(Utf8JsonWriter writer, DoctorGitReport git)
    {
        writer.WriteStartObject("git");
        writer.WriteBoolean("available", git.Available);
        WriteNullableString(writer, "path", git.Path);
        WriteNullableString(writer, "version", git.Version);
        writer.WriteBoolean("meetsMinimumVersion", git.MeetsMinimumVersion);
        writer.WriteStartArray("capabilities");
        foreach (var capability in git.Capabilities)
        {
            writer.WriteStartObject();
            writer.WriteString("name", Sanitize(capability.Name));
            writer.WriteBoolean("available", capability.Available);
            writer.WriteString("requirement", Sanitize(capability.Requirement));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteNullableString(writer, "error", git.Error);
        writer.WriteEndObject();
    }

    private static void WriteRepository(
        Utf8JsonWriter writer,
        DoctorRepositoryReport repository)
    {
        writer.WriteStartObject("repository");
        writer.WriteBoolean("available", repository.Available);
        WriteNullableString(writer, "workTree", repository.WorkTree);
        WriteNullableString(writer, "gitDirectory", repository.GitDirectory);
        if (repository.IsBare is { } isBare)
        {
            writer.WriteBoolean("isBare", isBare);
        }
        else
        {
            writer.WriteNull("isBare");
        }

        WriteNullableString(writer, "objectFormat", repository.ObjectFormat);
        writer.WriteString("trust", Sanitize(repository.Trust));
        WriteNullableString(writer, "error", repository.Error);
        writer.WriteEndObject();
    }

    private static void WriteTool(Utf8JsonWriter writer, DoctorToolReport tool)
    {
        writer.WriteStartObject(tool.Name);
        writer.WriteBoolean("available", tool.Available);
        WriteNullableString(writer, "path", tool.Path);
        WriteNullableString(writer, "version", tool.Version);
        WriteNullableString(writer, "error", tool.Error);
        writer.WriteEndObject();
    }

    private static void WriteStorage(
        Utf8JsonWriter writer,
        DoctorStorageReport storage)
    {
        writer.WriteStartObject("storage");
        WritePath(writer, storage.Configuration);
        WritePath(writer, storage.Cache);
        WritePath(writer, storage.State);
        WritePath(writer, storage.Traces);
        WriteNullableString(writer, "error", storage.Error);
        writer.WriteEndObject();
    }

    private static void WritePath(Utf8JsonWriter writer, DoctorPathReport path)
    {
        writer.WriteStartObject(path.Name);
        WriteNullableString(writer, "path", path.Path);
        writer.WriteString("status", Sanitize(path.Status));
        writer.WriteEndObject();
    }

    private static void WritePath(TextWriter output, DoctorPathReport path)
        => output.WriteLine(
            $"{char.ToUpperInvariant(path.Name[0])}{path.Name[1..]} directory: " +
            $"{Sanitize(path.Path ?? "unavailable")} ({Sanitize(path.Status)})");

    private static void WriteTool(
        TextWriter output,
        string label,
        DoctorToolReport tool)
    {
        if (!tool.Available)
        {
            output.WriteLine($"{label}: unavailable ({Sanitize(tool.Error ?? "unknown error")})");
            return;
        }

        var version = tool.Version is null ? string.Empty : $" {Sanitize(tool.Version)}";
        var error = tool.Error is null ? string.Empty : $" ({Sanitize(tool.Error)})";
        output.WriteLine($"{label}:{version} ({Sanitize(tool.Path!)}){error}");
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, Sanitize(value));
        }
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteNumber(propertyName, value.Value);
        }
    }

    private static string Sanitize(string value)
        => TerminalTextSanitizer.Sanitize(value);
}
