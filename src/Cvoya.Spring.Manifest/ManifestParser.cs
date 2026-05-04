// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;
using System.IO;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Parses unit manifest YAML into <see cref="UnitManifest"/> instances and
/// reports which tolerated-but-unapplied sections are present. Shared by the
/// <c>spring apply</c> CLI and the package-install activator so both code
/// paths use the same grammar and warning text.
/// </summary>
public static class ManifestParser
{
    /// <summary>
    /// Sections of the unit manifest grammar that are parsed but not yet
    /// applied by the platform. Listed here so consumers emit a consistent
    /// warning per section.
    /// </summary>
    /// <remarks>
    /// <c>execution</c> left this list in the B-wide implementation of
    /// #601 / #603 / #409 — the unit's execution block is now persisted
    /// and resolved as agent-level default (see
    /// <c>UnitCreationService.PersistUnitDefinitionExecutionAsync</c> and
    /// <c>IUnitExecutionStore</c>).
    /// </remarks>
    public static readonly IReadOnlyList<string> UnsupportedSections = new[]
    {
        "ai", "connectors", "policies", "humans",
    };

    /// <summary>
    /// Parses the manifest YAML text into a <see cref="UnitManifest"/>.
    /// Throws <see cref="ManifestParseException"/> if the document is malformed
    /// or the required <c>unit.name</c> field is missing.
    /// </summary>
    public static UnitManifest Parse(string yamlText)
    {
        ManifestDocument? doc;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            doc = deserializer.Deserialize<ManifestDocument>(yamlText);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ManifestParseException($"Invalid YAML: {ex.Message}", ex);
        }

        if (doc?.Unit is null)
        {
            throw new ManifestParseException("Manifest is missing the required 'unit' root section.");
        }

        if (string.IsNullOrWhiteSpace(doc.Unit.Name))
        {
            throw new ManifestParseException("Manifest is missing the required 'unit.name' field.");
        }

        // #1629 PR7: validate the manifest grammar — reject path-style refs
        // (the slug-as-PK era is over) and ensure member symbols are unique
        // within the file. Cross-package references that parse as Guids are
        // accepted; bare strings are treated as local symbols.
        ValidateUnitMemberGrammar(doc.Unit);

        return doc.Unit;
    }

    /// <summary>
    /// Validates the <c>members:</c> list against the v0.1 manifest grammar:
    /// <list type="bullet">
    ///   <item><description>
    ///     Path-style references (<c>scheme://...</c>) are rejected — the
    ///     slug-as-PK era is over (#1629). Authors use local symbols within
    ///     the manifest and 32-char no-dash hex Guids across packages.
    ///   </description></item>
    ///   <item><description>
    ///     Each member resolves to either an <c>agent:</c> or <c>unit:</c>
    ///     reference, not both, not neither.
    ///   </description></item>
    ///   <item><description>
    ///     Within a single unit's member list, the resolved symbol (the
    ///     reference value) must be unique — two entries naming the same
    ///     peer artefact is a manifest authoring error.
    ///   </description></item>
    /// </list>
    /// </summary>
    private static void ValidateUnitMemberGrammar(UnitManifest unit)
    {
        if (unit.Members is null || unit.Members.Count == 0)
        {
            return;
        }

        var seenSymbols = new HashSet<string>(System.StringComparer.Ordinal);
        for (var i = 0; i < unit.Members.Count; i++)
        {
            var member = unit.Members[i];

            LocalSymbolValidator.RejectPathStyleReference(member.Agent, $"unit.members[{i}].agent");
            LocalSymbolValidator.RejectPathStyleReference(member.Unit, $"unit.members[{i}].unit");

            var hasAgent = !string.IsNullOrWhiteSpace(member.Agent);
            var hasUnit = !string.IsNullOrWhiteSpace(member.Unit);

            if (hasAgent && hasUnit)
            {
                throw new ManifestParseException(
                    $"unit.members[{i}] declares both 'agent' and 'unit'; " +
                    "a single member entry must reference exactly one peer artefact.");
            }

            if (!hasAgent && !hasUnit)
            {
                throw new ManifestParseException(
                    $"unit.members[{i}] is missing both 'agent' and 'unit'; " +
                    "every member entry must reference exactly one peer artefact " +
                    "by local symbol or 32-char no-dash hex Guid.");
            }

            var symbol = (member.Agent ?? member.Unit)!.Trim();
            if (!seenSymbols.Add(symbol))
            {
                throw new ManifestParseException(
                    $"unit.members lists '{symbol}' more than once. " +
                    "Each member symbol must be unique within a unit's member list.");
            }
        }
    }

    /// <summary>
    /// Parses the manifest at <paramref name="filePath"/> and returns the resolved unit manifest.
    /// </summary>
    public static UnitManifest ParseFile(string filePath)
    {
        var text = File.ReadAllText(filePath);
        return Parse(text);
    }

    /// <summary>
    /// Returns the list of <see cref="UnsupportedSections"/> that are actually
    /// populated on <paramref name="manifest"/>. Both the CLI and the API use
    /// this to surface "parsed but not yet applied" warnings.
    /// </summary>
    public static IReadOnlyList<string> CollectUnsupportedSections(UnitManifest manifest)
    {
        var present = new List<string>();
        foreach (var section in UnsupportedSections)
        {
            if (IsSectionPresent(manifest, section))
            {
                present.Add(section);
            }
        }
        return present;
    }

    private static bool IsSectionPresent(UnitManifest manifest, string section) => section switch
    {
        "ai" => manifest.Ai is not null,
        "connectors" => manifest.Connectors is { Count: > 0 },
        "policies" => manifest.Policies is { Count: > 0 },
        "humans" => manifest.Humans is { Count: > 0 },
        _ => false,
    };
}

/// <summary>
/// Thrown when a manifest YAML document cannot be parsed into a valid
/// <see cref="UnitManifest"/>.
/// </summary>
public class ManifestParseException : System.Exception
{
    /// <summary>Creates a new <see cref="ManifestParseException"/>.</summary>
    public ManifestParseException(string message) : base(message) { }

    /// <summary>Creates a new <see cref="ManifestParseException"/> with an inner cause.</summary>
    public ManifestParseException(string message, System.Exception inner) : base(message, inner) { }
}