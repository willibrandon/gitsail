using System.Collections.Immutable;
using System.Text.Json;

namespace GitSail.Domain;

/// <summary>
/// Validates and writes the versioned user-global executable capability grant format.
/// </summary>
internal static class ExecutableCapabilityGrantFormat
{
    private const int MaximumSerializedCharacters = 32 * 1024;

    /// <summary>
    /// Gets the maximum number of exact command hashes retained for one repository.
    /// </summary>
    internal const int MaximumGrantedCommands = 256;

    /// <summary>
    /// Parses one strict version-1 grant without accepting unknown fields or duplicate hashes.
    /// </summary>
    /// <param name="text">The exact UTF-8-decoded JSON text.</param>
    /// <param name="commandHashes">The unique lowercase command hashes when valid.</param>
    /// <param name="error">The actionable validation error when invalid.</param>
    /// <returns><see langword="true"/> when the complete grant is valid.</returns>
    internal static bool TryParse(
        string text,
        out ImmutableHashSet<string> commandHashes,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumSerializedCharacters)
        {
            commandHashes = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
            error = "The executable capability grant exceeds the supported size.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            var root = document.RootElement;
            var properties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToArray()
                : [];
            if (root.ValueKind != JsonValueKind.Object ||
                properties.Length != 2 ||
                properties.Count(static property => property.Name == "version") != 1 ||
                properties.Count(static property => property.Name == "commands") != 1 ||
                !root.TryGetProperty("version", out var version) ||
                !version.TryGetInt32(out var versionValue) || versionValue != 1 ||
                !root.TryGetProperty("commands", out var commands) ||
                commands.ValueKind != JsonValueKind.Array ||
                commands.GetArrayLength() > MaximumGrantedCommands)
            {
                commandHashes = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
                error = "The executable capability grant must be a version-1 object with a bounded commands array.";
                return false;
            }

            var hashes = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var command in commands.EnumerateArray())
            {
                if (command.ValueKind != JsonValueKind.String ||
                    command.GetString() is not { } hash ||
                    !IsCommandHash(hash) ||
                    !hashes.Add(hash))
                {
                    commandHashes = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
                    error = "The executable capability grant contains an invalid or duplicate command hash.";
                    return false;
                }
            }

            commandHashes = hashes.ToImmutable();
            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            commandHashes = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
            error = $"The executable capability grant is invalid JSON: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Writes one canonical compact version-1 grant with hashes in ordinal order.
    /// </summary>
    /// <param name="commandHashes">The unique lowercase SHA-256 command hashes.</param>
    /// <returns>The canonical UTF-8 JSON bytes.</returns>
    internal static byte[] Serialize(ImmutableHashSet<string> commandHashes)
    {
        ArgumentNullException.ThrowIfNull(commandHashes);
        if (commandHashes.Count > MaximumGrantedCommands ||
            commandHashes.Any(static hash => !IsCommandHash(hash)))
        {
            throw new ArgumentException(
                "Executable capability hashes must be bounded lowercase SHA-256 values.",
                nameof(commandHashes));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WritePropertyName("commands");
            writer.WriteStartArray();
            foreach (var hash in commandHashes.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(hash);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Determines whether text is one canonical lowercase SHA-256 command hash.
    /// </summary>
    /// <param name="value">The candidate hash text.</param>
    /// <returns><see langword="true"/> when the text has the exact required form.</returns>
    internal static bool IsCommandHash(string value)
        => value.Length == 64 &&
            value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
