using GitSail.Domain;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Decodes hostile transport bytes into line-preserving, URL-redacted, terminal-safe text.
/// </summary>
internal static class TransportTextFormatter
{
    /// <summary>
    /// Formats one bounded transport channel without exposing configured URL credentials.
    /// </summary>
    /// <param name="bytes">The exact bounded child-output bytes.</param>
    /// <param name="catalog">The exact remote catalog whose configured URLs must be redacted.</param>
    /// <returns>Line-preserving text safe for an ordinary terminal editor presentation.</returns>
    internal static string Format(ReadOnlySpan<byte> bytes, RemoteCatalog catalog)
        => Format(bytes, catalog, []);

    /// <summary>
    /// Formats one bounded transport channel while also redacting resolved effective URLs.
    /// </summary>
    /// <param name="bytes">The exact bounded child-output bytes.</param>
    /// <param name="catalog">The exact remote catalog whose configured URLs must be redacted.</param>
    /// <param name="additionalUrls">Additional effective transport URLs whose credentials must be redacted.</param>
    /// <returns>Line-preserving text safe for an ordinary terminal editor presentation.</returns>
    internal static string Format(
        ReadOnlySpan<byte> bytes,
        RemoteCatalog catalog,
        IReadOnlyList<RemoteUrl> additionalUrls)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(additionalUrls);
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var remote in catalog.Remotes)
        {
            text = Redact(text, remote.FetchUrls);
            text = Redact(text, remote.PushUrls);
        }

        text = Redact(text, additionalUrls);

        return TerminalOutputFormatter.Format(text);
    }

    private static string Redact(string text, IReadOnlyList<RemoteUrl> urls)
    {
        foreach (var url in urls)
        {
            text = url.RedactFrom(text);
        }

        return text;
    }
}
