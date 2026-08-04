using GitSail.Domain;
using System.Buffers;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Parses exact effective Git URLs into isolated local-path or structured SSH targets.
/// </summary>
internal static class RemoteInitializationTargetParser
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Parses one exact effective URL without treating user data as command syntax.
    /// </summary>
    /// <param name="url">The exact effective remote URL.</param>
    /// <returns>The validated local or SSH initialization target.</returns>
    internal static RemoteInitializationTarget Parse(RemoteUrl url)
    {
        ArgumentNullException.ThrowIfNull(url);
        string text;
        try
        {
            text = s_strictUtf8.GetString(url.GetBytes());
        }
        catch (DecoderFallbackException)
        {
            throw new RemoteInitializationException(
                "Remote initialization requires a URL representable as valid Unicode text.");
        }

        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0', StringComparison.Ordinal))
        {
            throw new RemoteInitializationException("The selected remote URL is empty or invalid.");
        }

        if (TryParseAbsoluteLocalPath(text, url, out var localTarget))
        {
            return localTarget;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !IsWindowsDriveUri(uri, text))
        {
            if (uri.IsFile)
            {
                return ParseFileUri(url, uri);
            }

            if (IsSshScheme(uri.Scheme))
            {
                return ParseSshUri(url, uri);
            }

            throw new RemoteInitializationException(
                "Remote initialization supports only absolute local paths, file URLs, SSH URLs, and SCP-style SSH destinations.");
        }

        return ParseScpStyle(url, text);
    }

    private static bool TryParseAbsoluteLocalPath(
        string text,
        RemoteUrl url,
        out RemoteInitializationTarget target)
    {
        var isAbsolute = Path.IsPathFullyQualified(text) ||
            (OperatingSystem.IsWindows() && IsWindowsDrivePath(text));
        if (!isAbsolute)
        {
            target = null!;
            return false;
        }

        var fullPath = Path.GetFullPath(text);
        target = new RemoteInitializationTarget(
            url,
            RemoteInitializationKind.Local,
            fullPath,
            sshDestination: null,
            sshPort: null,
            remotePath: null);
        return true;
    }

    private static RemoteInitializationTarget ParseFileUri(RemoteUrl url, Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new RemoteInitializationException(
                "A local file URL for initialization cannot contain user information, a query, or a fragment.");
        }

        if (!OperatingSystem.IsWindows() &&
            !string.IsNullOrEmpty(uri.Host) &&
            !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteInitializationException(
                "A non-local file URL host cannot be initialized as a local repository on this platform.");
        }

        var path = Path.GetFullPath(uri.LocalPath);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new RemoteInitializationException("The file URL did not resolve to an absolute platform path.");
        }

        return new RemoteInitializationTarget(
            url,
            RemoteInitializationKind.Local,
            path,
            sshDestination: null,
            sshPort: null,
            remotePath: null);
    }

    private static RemoteInitializationTarget ParseSshUri(RemoteUrl url, Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new RemoteInitializationException(
                "The SSH URL requires one host and cannot contain a query or fragment.");
        }

        var userInfo = Uri.UnescapeDataString(uri.UserInfo);
        if (userInfo.Contains(':', StringComparison.Ordinal))
        {
            throw new RemoteInitializationException(
                "SSH URL passwords are not accepted; use an SSH agent or authenticated prompt instead.");
        }

        var pathBytes = DecodeUriPath(uri.AbsolutePath);
        if (pathBytes.Length == 0 || pathBytes.Contains((byte)0))
        {
            throw new RemoteInitializationException("The SSH URL requires one nonempty remote repository path.");
        }

        var destination = new SshDestination(
            string.IsNullOrEmpty(userInfo) ? null : userInfo,
            uri.Host,
            uri.HostNameType is UriHostNameType.IPv6);
        return new RemoteInitializationTarget(
            url,
            RemoteInitializationKind.Ssh,
            localPath: null,
            destination,
            uri.IsDefaultPort ? null : uri.Port,
            pathBytes);
    }

    private static RemoteInitializationTarget ParseScpStyle(RemoteUrl url, string text)
    {
        var separator = FindScpSeparator(text);
        if (separator <= 0 || separator == text.Length - 1)
        {
            throw new RemoteInitializationException(
                "The selected URL is neither an absolute local path nor a valid SSH destination.");
        }

        var destinationText = text[..separator];
        if (destinationText.Contains('/') || destinationText.Contains('\\'))
        {
            throw new RemoteInitializationException("An SCP-style SSH destination contains an invalid host.");
        }

        var at = destinationText.LastIndexOf('@');
        var user = at < 0 ? null : destinationText[..at];
        var hostText = at < 0 ? destinationText : destinationText[(at + 1)..];
        var isIpv6 = hostText.Length >= 2 && hostText[0] == '[' && hostText[^1] == ']';
        var host = isIpv6 ? hostText[1..^1] : hostText;
        var remotePath = text[(separator + 1)..];
        if (remotePath.Contains('\0', StringComparison.Ordinal))
        {
            throw new RemoteInitializationException("The SSH remote repository path contains NUL.");
        }

        return new RemoteInitializationTarget(
            url,
            RemoteInitializationKind.Ssh,
            localPath: null,
            new SshDestination(user, host, isIpv6),
            sshPort: null,
            s_strictUtf8.GetBytes(remotePath));
    }

    private static int FindScpSeparator(string text)
    {
        var bracketDepth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            bracketDepth += text[index] switch
            {
                '[' => 1,
                ']' => -1,
                _ => 0,
            };
            if (bracketDepth < 0)
            {
                return -1;
            }

            if (text[index] == ':' && bracketDepth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsSshScheme(string scheme)
        => string.Equals(scheme, "ssh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(scheme, "git+ssh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(scheme, "ssh+git", StringComparison.OrdinalIgnoreCase);

    private static byte[] DecodeUriPath(string escapedPath)
    {
        var output = new ArrayBufferWriter<byte>(escapedPath.Length);
        for (var index = 0; index < escapedPath.Length;)
        {
            if (escapedPath[index] == '%' && index + 2 < escapedPath.Length &&
                TryParseHexNibble(escapedPath[index + 1], out var high) &&
                TryParseHexNibble(escapedPath[index + 2], out var low))
            {
                output.GetSpan(1)[0] = (byte)((high << 4) | low);
                output.Advance(1);
                index += 3;
                continue;
            }

            var status = Rune.DecodeFromUtf16(
                escapedPath.AsSpan(index),
                out var rune,
                out var consumed);
            if (status != OperationStatus.Done)
            {
                throw new RemoteInitializationException(
                    "The SSH URL path contains invalid Unicode text.");
            }

            var destination = output.GetSpan(rune.Utf8SequenceLength);
            _ = rune.EncodeToUtf8(destination);
            output.Advance(rune.Utf8SequenceLength);
            index += consumed;
        }

        return output.WrittenSpan.ToArray();
    }

    private static bool TryParseHexNibble(char value, out int nibble)
    {
        nibble = value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'A' and <= 'F' => value - 'A' + 10,
            >= 'a' and <= 'f' => value - 'a' + 10,
            _ => -1,
        };
        return nibble >= 0;
    }

    private static bool IsWindowsDrivePath(string text)
        => text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' &&
            text[2] is '\\' or '/';

    private static bool IsWindowsDriveUri(Uri uri, string text)
        => uri.Scheme.Length == 1 && IsWindowsDrivePath(text);
}
