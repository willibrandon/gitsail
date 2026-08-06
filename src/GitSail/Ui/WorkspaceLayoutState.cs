using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace GitSail.Ui;

/// <summary>
/// Retains the versioned workspace layout while preserving fields owned by other layout features.
/// </summary>
internal sealed class WorkspaceLayoutState
{
    private const int CurrentVersion = 1;
    private const int MaximumPinnedMenus = 16;
    private readonly ImmutableArray<KeyValuePair<string, JsonElement>> _retainedProperties;

    private WorkspaceLayoutState(
        ImmutableArray<PinnedMenuLayout> pinnedMenus,
        ImmutableArray<KeyValuePair<string, JsonElement>> retainedProperties)
    {
        PinnedMenus = pinnedMenus;
        _retainedProperties = retainedProperties;
    }

    /// <summary>
    /// Gets an empty version-1 workspace layout.
    /// </summary>
    internal static WorkspaceLayoutState Empty { get; } = new([], []);

    /// <summary>
    /// Gets the complete ordered set of persisted menu windows.
    /// </summary>
    internal ImmutableArray<PinnedMenuLayout> PinnedMenus { get; }

    /// <summary>
    /// Parses one versioned layout without accepting duplicate or ambiguous JSON fields.
    /// </summary>
    /// <param name="text">The exact effective layout value, or no value.</param>
    /// <param name="state">The parsed layout when successful.</param>
    /// <returns><see langword="true"/> when the value is absent or a valid version-1 layout.</returns>
    internal static bool TryParse(string? text, out WorkspaceLayoutState state)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            state = Empty;
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                state = Empty;
                return false;
            }

            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            var retained = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonElement>>();
            var pinnedMenus = ImmutableArray<PinnedMenuLayout>.Empty;
            var hasVersion = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    state = Empty;
                    return false;
                }

                if (property.NameEquals("version"))
                {
                    hasVersion = property.Value.ValueKind == JsonValueKind.Number &&
                        property.Value.TryGetInt32(out var version) &&
                        version == CurrentVersion;
                    if (!hasVersion)
                    {
                        state = Empty;
                        return false;
                    }

                    continue;
                }

                if (property.NameEquals("pinnedMenus"))
                {
                    if (!TryParsePinnedMenus(property.Value, out pinnedMenus))
                    {
                        state = Empty;
                        return false;
                    }

                    continue;
                }

                retained.Add(KeyValuePair.Create(property.Name, property.Value.Clone()));
            }

            if (!hasVersion)
            {
                state = Empty;
                return false;
            }

            state = new WorkspaceLayoutState(pinnedMenus, retained.ToImmutable());
            return true;
        }
        catch (JsonException)
        {
            state = Empty;
            return false;
        }
    }

    /// <summary>
    /// Returns the persisted geometry for one stable menu identity.
    /// </summary>
    /// <param name="id">The stable menu identity.</param>
    /// <returns>The matching geometry, or no value when the menu is not pinned.</returns>
    internal PinnedMenuLayout? FindPinnedMenu(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        foreach (var menu in PinnedMenus)
        {
            if (string.Equals(menu.Id, id, StringComparison.Ordinal))
            {
                return menu;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds or replaces one pinned menu while retaining every unrelated layout property.
    /// </summary>
    /// <param name="menu">The stable identity and settled geometry to retain.</param>
    /// <returns>A new version-1 layout containing the menu.</returns>
    internal WorkspaceLayoutState WithPinnedMenu(PinnedMenuLayout menu)
    {
        if (!IsValidMenu(menu))
        {
            throw new ArgumentOutOfRangeException(nameof(menu));
        }

        var menus = PinnedMenus
            .Where(candidate => !string.Equals(candidate.Id, menu.Id, StringComparison.Ordinal))
            .Append(menu)
            .OrderBy(static candidate => candidate.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        if (menus.Length > MaximumPinnedMenus)
        {
            throw new InvalidOperationException($"A layout cannot retain more than {MaximumPinnedMenus} pinned menus.");
        }

        return new WorkspaceLayoutState(menus, _retainedProperties);
    }

    /// <summary>
    /// Removes one pinned menu while retaining every unrelated layout property.
    /// </summary>
    /// <param name="id">The stable menu identity to remove.</param>
    /// <returns>A new version-1 layout without the menu.</returns>
    internal WorkspaceLayoutState WithoutPinnedMenu(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new WorkspaceLayoutState(
            [.. PinnedMenus.Where(menu => !string.Equals(menu.Id, id, StringComparison.Ordinal))],
            _retainedProperties);
    }

    /// <summary>
    /// Serializes the complete version-1 layout without reflection-based metadata.
    /// </summary>
    /// <returns>Compact deterministic UTF-8 JSON suitable for Git configuration.</returns>
    internal string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);
            foreach (var property in _retainedProperties.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Key);
                property.Value.WriteTo(writer);
            }

            writer.WriteStartArray("pinnedMenus");
            foreach (var menu in PinnedMenus)
            {
                writer.WriteStartObject();
                writer.WriteString("id", menu.Id);
                writer.WriteNumber("x", menu.X);
                writer.WriteNumber("y", menu.Y);
                writer.WriteNumber("width", menu.Width);
                writer.WriteNumber("height", menu.Height);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool TryParsePinnedMenus(
        JsonElement element,
        out ImmutableArray<PinnedMenuLayout> pinnedMenus)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaximumPinnedMenus)
        {
            pinnedMenus = [];
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var menus = ImmutableArray.CreateBuilder<PinnedMenuLayout>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryParsePinnedMenu(item, out var menu) || !ids.Add(menu.Id))
            {
                pinnedMenus = [];
                return false;
            }

            menus.Add(menu);
        }

        pinnedMenus = [.. menus.OrderBy(static menu => menu.Id, StringComparer.Ordinal)];
        return true;
    }

    private static bool TryParsePinnedMenu(JsonElement element, out PinnedMenuLayout menu)
    {
        menu = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? id = null;
        int? x = null;
        int? y = null;
        int? width = null;
        int? height = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return false;
            }

            if (property.NameEquals("id") && property.Value.ValueKind == JsonValueKind.String)
            {
                id = property.Value.GetString();
            }
            else if (property.NameEquals("x") && property.Value.TryGetInt32(out var parsedX))
            {
                x = parsedX;
            }
            else if (property.NameEquals("y") && property.Value.TryGetInt32(out var parsedY))
            {
                y = parsedY;
            }
            else if (property.NameEquals("width") && property.Value.TryGetInt32(out var parsedWidth))
            {
                width = parsedWidth;
            }
            else if (property.NameEquals("height") && property.Value.TryGetInt32(out var parsedHeight))
            {
                height = parsedHeight;
            }
            else
            {
                return false;
            }
        }

        if (id is null || x is null || y is null || width is null || height is null)
        {
            return false;
        }

        menu = new PinnedMenuLayout(id, x.Value, y.Value, width.Value, height.Value);
        return IsValidMenu(menu);
    }

    private static bool IsValidMenu(PinnedMenuLayout menu)
        => menu.Id.Length is > 0 and <= 128 &&
            menu.Id.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') &&
            menu.X is >= 0 and <= 4096 &&
            menu.Y is >= 0 and <= 4096 &&
            menu.Width is >= 20 and <= 512 &&
            menu.Height is >= 8 and <= 512;
}
