// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Net;

/// <summary>
/// Canonical URL-join helper (#2707). Concatenates a base URL and a relative
/// path while normalising the boundary slash so the result never carries the
/// double-slash classes (<c>http://host//path</c>, <c>http://host/api//path</c>)
/// that the engineer-hallucination cascade in #2707 surfaced.
/// </summary>
/// <remarks>
/// <para>
/// Every site in runtime / connector code that needs to compute
/// <c>base + path</c> should call <see cref="Combine"/> rather than splicing
/// strings inline. The pre-existing <c>base.TrimEnd('/') + "/path"</c>
/// idiom in OSS callers produces the same result in the common case but
/// silently mis-handles edge inputs (empty path, base without scheme, path
/// already a fully-qualified URL); routing every concatenation through this
/// helper closes the variance and gives the audit a single line to inspect.
/// </para>
/// <para>
/// The helper is deliberately string-typed at the boundary because the
/// surrounding code is overwhelmingly string-based — HTTP clients build
/// Uris from strings, options carry <c>BaseUrl</c> as <c>string</c>, and
/// log lines render strings. A <see cref="Uri"/>-typed overload is not
/// added because the caller-side conversion would only push the slash-
/// normalisation problem one frame down.
/// </para>
/// </remarks>
public static class UrlPath
{
    /// <summary>
    /// Combines a base URL and a relative path with exactly one slash at
    /// the boundary. Preserves <paramref name="path"/>'s query string and
    /// fragment intact; preserves <paramref name="baseUrl"/>'s query string
    /// and fragment when <paramref name="path"/> is empty.
    /// </summary>
    /// <remarks>
    /// Behaviour by input shape:
    /// <list type="bullet">
    ///   <item><description>
    ///     <paramref name="baseUrl"/> trailing slashes are collapsed to one
    ///     (or zero if <paramref name="path"/> is empty); leading slashes
    ///     on <paramref name="path"/> are collapsed to one (or zero if it
    ///     follows nothing).
    ///   </description></item>
    ///   <item><description>
    ///     An empty <paramref name="path"/> returns <paramref name="baseUrl"/>
    ///     verbatim — no trailing slash is added.
    ///   </description></item>
    ///   <item><description>
    ///     A null or whitespace <paramref name="baseUrl"/> returns
    ///     <paramref name="path"/> verbatim (the caller asked for the path
    ///     against an empty base; we don't synthesise a scheme).
    ///   </description></item>
    ///   <item><description>
    ///     A <paramref name="path"/> that is itself an absolute URL
    ///     (<c>http://</c>, <c>https://</c>) is returned unchanged — the
    ///     "path" was already the destination.
    ///   </description></item>
    /// </list>
    /// </remarks>
    /// <param name="baseUrl">
    /// The base URL — may carry any number of trailing slashes; may include
    /// a path component, query string, or fragment. The query string and
    /// fragment travel with <paramref name="path"/> when one is supplied.
    /// </param>
    /// <param name="path">
    /// The relative path to append — may carry any number of leading
    /// slashes. May include a query string (<c>?key=value</c>) or fragment
    /// (<c>#anchor</c>); those segments pass through unchanged.
    /// </param>
    /// <returns>The combined URL string.</returns>
    public static string Combine(string? baseUrl, string? path)
    {
        var basePart = baseUrl ?? string.Empty;
        var pathPart = path ?? string.Empty;

        if (string.IsNullOrWhiteSpace(basePart))
        {
            return pathPart;
        }
        if (string.IsNullOrEmpty(pathPart))
        {
            return basePart;
        }

        // If the relative path is itself an absolute URL, the caller passed
        // an already-resolved destination through — return it unchanged.
        // Matches new Uri(base, absoluteHref) semantics.
        if (IsAbsoluteHttpUrl(pathPart))
        {
            return pathPart;
        }

        var trimmedBase = TrimTrailingSlashes(basePart);
        var trimmedPath = TrimLeadingSlashes(pathPart);
        return trimmedBase + "/" + trimmedPath;
    }

    private static string TrimTrailingSlashes(string value)
    {
        var end = value.Length;
        while (end > 0 && value[end - 1] == '/')
        {
            end--;
        }
        return end == value.Length ? value : value[..end];
    }

    private static string TrimLeadingSlashes(string value)
    {
        var start = 0;
        while (start < value.Length && value[start] == '/')
        {
            start++;
        }
        return start == 0 ? value : value[start..];
    }

    private static bool IsAbsoluteHttpUrl(string value)
    {
        // Cheap prefix check avoids the cost of a full Uri parse and keeps
        // the helper allocation-free on the hot path. Casing rules per
        // RFC 3986 §3.1: schemes are case-insensitive.
        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}
