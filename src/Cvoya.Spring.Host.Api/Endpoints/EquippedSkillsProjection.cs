// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Collections.Generic;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Shared projection between the unit and agent equipped-skill endpoints
/// (#2360). Maps a resolved <see cref="SkillBundle"/> list to the flat
/// wire shape the operator UIs consume.
/// </summary>
internal static class EquippedSkillsProjection
{
    /// <summary>Maximum length of the prompt summary surfaced in the listing.</summary>
    internal const int PromptSummaryMaxLength = 200;

    /// <summary>
    /// Projects a bundle list into the response shape. Declaration order
    /// is preserved.
    /// </summary>
    public static IReadOnlyList<EquippedSkillEntry> From(IReadOnlyList<SkillBundle> bundles)
    {
        if (bundles.Count == 0)
        {
            return System.Array.Empty<EquippedSkillEntry>();
        }

        var entries = new List<EquippedSkillEntry>(bundles.Count);
        foreach (var bundle in bundles)
        {
            entries.Add(new EquippedSkillEntry(
                PackageName: bundle.PackageName,
                SkillName: bundle.SkillName,
                PromptSummary: Summarise(bundle.Prompt),
                RequiredTools: bundle.RequiredTools
                    .Select(t => new EquippedSkillToolRequirement(
                        Name: t.Name,
                        Description: t.Description,
                        Optional: t.Optional))
                    .ToList()));
        }
        return entries;
    }

    private static string Summarise(string prompt)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            return string.Empty;
        }

        // First non-empty line, capped at PromptSummaryMaxLength chars. The
        // body is markdown and the typical first line is a heading; the
        // summary should be informative without dragging the entire body
        // through a list-listing surface (callers paginate per subject so
        // short summaries keep payloads predictable).
        var trimmed = prompt.TrimStart();
        var newlineIdx = trimmed.IndexOfAny(['\n', '\r']);
        var firstLine = newlineIdx < 0 ? trimmed : trimmed[..newlineIdx];
        firstLine = firstLine.Trim();
        if (firstLine.Length > PromptSummaryMaxLength)
        {
            return firstLine[..PromptSummaryMaxLength];
        }
        return firstLine;
    }
}
