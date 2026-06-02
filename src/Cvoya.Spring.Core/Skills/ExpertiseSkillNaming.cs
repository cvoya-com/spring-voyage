// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// Slug helper for the expertise directory. Derives a case-folded, path-safe
/// slug from an expertise-domain name so the directory-search surface
/// (<c>POST /api/v1/directory/search</c> → <c>IExpertiseSearch</c>) can key,
/// match, and address hits by a stable token instead of the raw display name.
/// </summary>
/// <remarks>
/// Pulled into a static class so callers that only need to derive a slug from a
/// domain name (the in-memory search index, the directory-search endpoint,
/// tests) don't take a dependency on any registry or service. The slug shape
/// also satisfies the canonical <see cref="ToolNaming.Pattern"/> (#2334) — a
/// case-folded run-collapsed identifier with no hyphens — so it composes
/// cleanly into any dotted-snake identifier a future surface may build from it.
/// </remarks>
public static class ExpertiseSkillNaming
{
    /// <summary>
    /// Lowercases the domain name and replaces any non-slug character with
    /// <c>_</c>, collapsing runs so <c>python/fastapi</c> → <c>python_fastapi</c>
    /// and <c>React / Next.js</c> → <c>react_next_js</c>. Empty input yields
    /// the empty string — callers that care about that case must guard before
    /// calling. The underscore separator satisfies the canonical
    /// <see cref="ToolNaming.Pattern"/> (#2334), which forbids hyphens in
    /// identifier segments.
    /// </summary>
    public static string Slugify(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(name.Length);
        var lastWasSeparator = true; // suppress leading separators
        foreach (var ch in name)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                buffer.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                buffer.Append('_');
                lastWasSeparator = true;
            }
        }

        // Trim any trailing separator.
        while (buffer.Length > 0 && buffer[^1] == '_')
        {
            buffer.Length--;
        }

        // A slug whose first character is a digit (e.g. domain name "9 to 5")
        // would break the segment's [a-z] leading-character rule if it is ever
        // composed into a dotted-snake identifier. Prefix with `x` so the
        // composed name still passes ToolNaming.Pattern; the prefix is harmless
        // in normal slugs (which start with a letter).
        if (buffer.Length > 0 && !char.IsAsciiLetter(buffer[0]))
        {
            buffer.Insert(0, 'x');
        }

        return buffer.ToString();
    }
}
