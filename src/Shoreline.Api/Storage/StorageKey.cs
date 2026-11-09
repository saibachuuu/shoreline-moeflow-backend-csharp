using System.Text;

namespace Shoreline.Api.Storage;

/// <summary>
/// Validation and URI encoding for the relative key space used by every backend.
/// Keys are not paths in S3, but the same safety checks keep Local storage and CDN
/// URLs deterministic and prevent a relative key from escaping its namespace.
/// </summary>
internal static class StorageKey
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    public static void ValidateObjectKey(string key, string parameterName = "key")
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);

        if (key.Length == 0)
        {
            throw new ArgumentException("Object key must not be empty.", parameterName);
        }

        if (key.Contains('\\'))
        {
            throw new ArgumentException("Object key must use '/' separators; backslashes are not allowed.", parameterName);
        }

        if (key.Contains('\0'))
        {
            throw new ArgumentException("Object key must not contain NUL characters.", parameterName);
        }

        if (key[0] == '/' || Path.IsPathRooted(key))
        {
            throw new ArgumentException("Object key must be relative to the storage namespace.", parameterName);
        }

        foreach (var segment in key.Split('/'))
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException("Object key must not contain '.' or '..' path segments.", parameterName);
            }
        }
    }

    public static void ValidateLocalObjectKey(string key, string parameterName = "key")
    {
        ValidateObjectKey(key, parameterName);

        foreach (var segment in key.Split('/'))
        {
            if (segment.Length == 0)
            {
                throw new ArgumentException("Local object keys must not contain empty path segments.", parameterName);
            }

            if (segment.IndexOfAny(InvalidFileNameChars) >= 0)
            {
                throw new ArgumentException("Local object key contains characters that are invalid in a file name.", parameterName);
            }
        }
    }

    /// <summary>
    /// Prefixes and list cursors may be empty, but must still be relative and free of
    /// traversal segments. A trailing slash is allowed for directory-style prefixes.
    /// </summary>
    public static void ValidatePrefix(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (value.Length == 0)
        {
            return;
        }

        if (value.Contains('\\'))
        {
            throw new ArgumentException("List prefix/cursor must use '/' separators.", parameterName);
        }

        if (value.Contains('\0'))
        {
            throw new ArgumentException("List prefix/cursor must not contain NUL characters.", parameterName);
        }

        if (value[0] == '/' || Path.IsPathRooted(value))
        {
            throw new ArgumentException("List prefix/cursor must be relative to the storage namespace.", parameterName);
        }

        foreach (var segment in value.Split('/'))
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException("List prefix/cursor must not contain '.' or '..' path segments.", parameterName);
            }
        }
    }

    /// <summary>
    /// Percent-encodes each path segment while preserving '/'. Unreserved characters
    /// are exactly RFC 3986 ALPHA / DIGIT / '-' / '.' / '_' / '~', matching the Python
    /// implementation's <c>urllib.parse.quote</c> behavior closely enough for CDN keys.
    /// </summary>
    public static string EncodePath(string value)
    {
        var segments = value.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = EncodeSegment(segments[i]);
        }

        return string.Join('/', segments);
    }

    private static string EncodeSegment(string segment)
    {
        var builder = new StringBuilder(segment.Length);
        foreach (var rune in segment.EnumerateRunes())
        {
            if (rune.Value < 128 && IsUnreserved((char)rune.Value))
            {
                builder.Append((char)rune.Value);
                continue;
            }

            foreach (var b in Encoding.UTF8.GetBytes(rune.ToString()))
            {
                builder.Append('%').Append(b.ToString("X2"));
            }
        }

        return builder.ToString();
    }

    private static bool IsUnreserved(char value) =>
        value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9'
        or '-' or '.' or '_' or '~';
}
