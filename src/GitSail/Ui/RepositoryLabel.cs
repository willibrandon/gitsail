using GitSail.Domain;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Creates compact control-safe repository labels for terminal headers.
/// </summary>
internal static class RepositoryLabel
{
    private const int MaximumHeaderRunes = 24;

    /// <summary>
    /// Returns the final displayed component of a worktree or bare repository path.
    /// </summary>
    /// <param name="repository">The discovered repository whose label is needed.</param>
    /// <returns>A compact repository label suitable for a constrained terminal row.</returns>
    internal static string Create(RepositoryLocation repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var path = repository.WorkTree?.DisplayText ?? repository.GitDirectory.DisplayText;
        var trimmed = path.TrimEnd('/', '\\');
        var separator = trimmed.LastIndexOfAny(['/', '\\']);
        var name = separator >= 0 && separator < trimmed.Length - 1
            ? trimmed[(separator + 1)..]
            : trimmed;
        var runes = name.EnumerateRunes().ToArray();
        if (runes.Length <= MaximumHeaderRunes)
        {
            return name;
        }

        var builder = new StringBuilder(MaximumHeaderRunes + 2);
        for (var index = 0; index < MaximumHeaderRunes - 3; index++)
        {
            builder.Append(runes[index]);
        }

        return builder.Append("...").ToString();
    }
}
