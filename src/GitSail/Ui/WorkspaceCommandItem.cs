using Hex1b;

namespace GitSail.Ui;

/// <summary>
/// Describes one searchable workspace action and its live availability.
/// </summary>
internal sealed class WorkspaceCommandItem
{
    /// <summary>
    /// Initializes one command-palette action with a stable identity and executor.
    /// </summary>
    /// <param name="id">The stable action identifier.</param>
    /// <param name="category">The user-facing action category.</param>
    /// <param name="label">The concise user-facing action name.</param>
    /// <param name="description">The complete user-facing action description.</param>
    /// <param name="binding">The current keyboard binding, or an empty string when unbound.</param>
    /// <param name="unavailableReason">The reason execution is disabled, or <see langword="null"/> when available.</param>
    /// <param name="executeAsync">The action executor, supplied with the active window manager.</param>
    /// <param name="menuCategories">The top-level menus containing the action, or <see langword="null"/> to use <paramref name="category"/>.</param>
    internal WorkspaceCommandItem(
        string id,
        string category,
        string label,
        string description,
        string binding,
        string? unavailableReason,
        Func<WindowManager, Task> executeAsync,
        IReadOnlyList<string>? menuCategories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(executeAsync);
        Id = id;
        Category = category;
        Label = label;
        Description = description;
        Binding = binding;
        UnavailableReason = unavailableReason;
        ExecuteAsync = executeAsync;
        MenuCategories = menuCategories ?? [category];
    }

    /// <summary>
    /// Gets the stable action identifier used to preserve palette focus.
    /// </summary>
    internal string Id { get; }

    /// <summary>
    /// Gets the user-facing action category.
    /// </summary>
    internal string Category { get; }

    /// <summary>
    /// Gets the concise user-facing action name.
    /// </summary>
    internal string Label { get; }

    /// <summary>
    /// Gets the complete user-facing action description.
    /// </summary>
    internal string Description { get; }

    /// <summary>
    /// Gets the current keyboard binding or an empty string when unbound.
    /// </summary>
    internal string Binding { get; }

    /// <summary>
    /// Gets the live unavailability reason or <see langword="null"/> when executable.
    /// </summary>
    internal string? UnavailableReason { get; }

    /// <summary>
    /// Gets whether the action is currently executable.
    /// </summary>
    internal bool IsAvailable => UnavailableReason is null;

    /// <summary>
    /// Gets the asynchronous action executor.
    /// </summary>
    internal Func<WindowManager, Task> ExecuteAsync { get; }

    /// <summary>
    /// Gets the top-level menus that present this same action identity.
    /// An action may appear in more than one useful menu without duplicating its handler.
    /// </summary>
    internal IReadOnlyList<string> MenuCategories { get; }

    /// <summary>
    /// Determines whether searchable action metadata contains the supplied filter.
    /// </summary>
    /// <param name="filter">The trimmed case-insensitive search text.</param>
    /// <returns><see langword="true"/> when any presented action field matches.</returns>
    internal bool Matches(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            Category.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            Description.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            Binding.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            MenuCategories.Any(category => category.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
            (UnavailableReason?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var binding = Binding.Length == 0 ? string.Empty : $" [{Binding}]";
        var availability = IsAvailable ? string.Empty : " [unavailable]";
        return $"{Category}: {Label}{binding}{availability}";
    }
}
