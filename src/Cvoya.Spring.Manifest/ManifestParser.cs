// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;
using System.IO;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Parses a per-artefact unit YAML document (ADR-0037 decision 1) into a
/// <see cref="UnitManifest"/>. Each unit YAML is a kind-discriminated
/// top-level document (<c>apiVersion: spring.voyage/v1</c>,
/// <c>kind: Unit</c>, <c>name</c>, <c>description</c>, …) with no
/// wrapping <c>unit:</c> key.
/// </summary>
/// <remarks>
/// v0.1 has no back-compat guarantees (issue #2406). The parser is strict:
/// unknown top-level fields are a parse error so typos and pre-v0.1
/// schema fragments fail at the same gate.
/// </remarks>
public static class ManifestParser
{
    /// <summary>
    /// Sections of the unit manifest grammar that are parsed but not yet
    /// applied by the platform.
    /// </summary>
    public static readonly IReadOnlyList<string> UnsupportedSections = new[]
    {
        "ai", "requires", "policies",
    };

    /// <summary>
    /// Parses the manifest YAML text into a <see cref="UnitManifest"/>.
    /// Throws <see cref="ManifestParseException"/> if the document is
    /// malformed or the required header fields are missing. Unknown
    /// top-level fields are rejected (strict parsing — issue #2406).
    /// </summary>
    public static UnitManifest Parse(string yamlText)
    {
        // ADR-0046 §1: the legacy top-level `humans:` block is removed.
        // The strict parser on UnitManifest would already reject the
        // unknown field, but the structured-error surface here gives the
        // operator an actionable migration hint pointing at this ADR.
        RejectLegacyHumansBlock(yamlText);

        UnitManifest? manifest;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new RequirementEntryYamlConverter())
                .WithTypeConverter(new InlineArtefactDefinitionYamlConverter())
                .Build();
            manifest = deserializer.Deserialize<UnitManifest>(yamlText);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ManifestParseException($"Invalid YAML: {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new ManifestParseException("Manifest is empty.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ApiVersion))
        {
            throw new ManifestParseException(
                "MissingApiVersion: every artefact YAML declares apiVersion: spring.voyage/v1 (ADR-0037 decision 1).");
        }

        if (string.IsNullOrWhiteSpace(manifest.Kind))
        {
            throw new ManifestParseException(
                "MissingKind: every artefact YAML declares kind: Unit/Agent/Skill (ADR-0037 decision 1).");
        }

        if (!string.Equals(manifest.Kind.Trim(), "Unit", System.StringComparison.Ordinal))
        {
            throw new ManifestParseException(
                $"Unit YAML declares kind: '{manifest.Kind}' but expected 'Unit'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new ManifestParseException(
                "Manifest is missing the required top-level 'name' field.");
        }

        ValidateUnitMemberGrammar(manifest);

        // Issue #2436: strict validation of execution.hosting on the unit
        // manifest. Unknown literals (e.g. `permanent`) are rejected at
        // parse time so the operator sees a precise diagnostic at install
        // time, not a silent fallback at dispatch time. Also normalises
        // the literal to lower-case so downstream comparisons can be
        // case-sensitive.
        if (manifest.Execution is { Hosting: { } unitHosting }
            && !string.IsNullOrWhiteSpace(unitHosting))
        {
            manifest.Execution.Hosting = NormaliseHostingLiteral(
                unitHosting,
                fieldLocation: "execution.hosting");
        }

        return manifest;
    }

    /// <summary>
    /// The set of valid hosting literals accepted on unit / agent /
    /// agent-template <c>execution.hosting</c> blocks (issue #2436).
    /// Comparison is case-insensitive; literals are normalised to
    /// lower-case after parsing so downstream consumers can compare
    /// directly with the enum string forms.
    /// </summary>
    /// <remarks>
    /// Matches the entries on <c>Cvoya.Spring.Core.Execution.AgentHostingMode</c>:
    /// <c>persistent</c>, <c>ephemeral</c>, <c>pooled</c>. The values are
    /// the lower-cased enum names verbatim.
    /// </remarks>
    public static readonly IReadOnlySet<string> ValidHostingLiterals =
        new HashSet<string>(System.StringComparer.Ordinal)
        {
            "persistent",
            "ephemeral",
            "pooled",
        };

    /// <summary>
    /// Validates <paramref name="hosting"/> against the
    /// <see cref="ValidHostingLiterals"/> set and returns the canonical
    /// lower-case form. Throws <see cref="ManifestParseException"/> on an
    /// unknown literal, naming the field path
    /// (<paramref name="fieldLocation"/>) and the rejected value so the
    /// operator can fix the YAML directly.
    /// </summary>
    internal static string NormaliseHostingLiteral(string hosting, string fieldLocation)
    {
        var trimmed = hosting.Trim();
        var lower = trimmed.ToLowerInvariant();
        if (!ValidHostingLiterals.Contains(lower))
        {
            throw new ManifestParseException(
                $"{fieldLocation}: unknown hosting literal '{trimmed}'. " +
                $"Expected one of: {string.Join(", ", ValidHostingLiterals)} (case-insensitive). " +
                "Issue #2436.");
        }
        return lower;
    }

    /// <summary>
    /// Validates the <c>members:</c> list against the v0.1 manifest grammar
    /// (ADR-0046 §1). Every entry carries exactly one of <c>agent:</c> /
    /// <c>unit:</c> / <c>human:</c>; the discriminator's value is either a
    /// bare scalar reference (ADR-0043 §5g) or an inline body. Humans are
    /// inline-only (ADR-0046 §6).
    /// </summary>
    private static void ValidateUnitMemberGrammar(UnitManifest unit)
    {
        if (unit.Members is null || unit.Members.Count == 0)
        {
            return;
        }

        var seenAgentUnitSymbols = new HashSet<string>(System.StringComparer.Ordinal);
        for (var i = 0; i < unit.Members.Count; i++)
        {
            var member = unit.Members[i];

            // Reject path-style references only on bare scalar forms; inline
            // bodies (ADR-0043 §5g) carry a full mapping rather than a
            // path-shaped scalar.
            if (member.Agent is { Reference: { } agentRef })
            {
                LocalSymbolValidator.RejectPathStyleReference(agentRef, $"unit.members[{i}].agent");
            }
            if (member.Unit is { Reference: { } unitRef })
            {
                LocalSymbolValidator.RejectPathStyleReference(unitRef, $"unit.members[{i}].unit");
            }
            if (member.Human is { Reference: { } humanRef })
            {
                // ADR-0046 §1: humans are addressable only by inline body
                // (with optional `from:` template chain). Bare-scalar
                // references to a peer human in the catalog are not
                // authored in v0.1 — the inline-only restriction is the
                // single concession §6 makes versus the agent / unit
                // grammar. Reject path-style scalars uniformly with the
                // sibling slots.
                LocalSymbolValidator.RejectPathStyleReference(humanRef, $"unit.members[{i}].human");
            }

            // Inline bodies (ADR-0043 §5g): require the body to declare a
            // name BEFORE the "missing both" / "declares both" checks so the
            // operator sees a precise diagnostic. The body's `name:` is the
            // local symbol the rest of the unit references, so an anonymous
            // inline body cannot be resolved by peers. This check runs first
            // because an anonymous inline body would otherwise look like an
            // empty slot to the "missing both" check below.
            //
            // Humans are exempt — a `human:` entry's identity is server-
            // allocated at install time (ADR-0046 §7 "fresh HumanEntity per
            // declaration") so the inline body does not need to carry a
            // local symbol the rest of the unit references.
            if (member.Agent is { IsInline: true } && IsBlankScalar(member.Agent))
            {
                throw new ManifestParseException(
                    $"unit.members[{i}].agent inline body is missing the required " +
                    "'name:' field (ADR-0043 §5g). The body's name is the local " +
                    "symbol the unit and its peers reference.");
            }
            if (member.Unit is { IsInline: true } && IsBlankScalar(member.Unit))
            {
                throw new ManifestParseException(
                    $"unit.members[{i}].unit inline body is missing the required " +
                    "'name:' field (ADR-0043 §5g). The body's name is the local " +
                    "symbol the unit and its peers reference.");
            }

            var hasAgent = member.Agent is not null && !IsBlankScalar(member.Agent);
            var hasUnit = member.Unit is not null && !IsBlankScalar(member.Unit);
            var hasHuman = member.Human is not null
                && !(member.Human.Reference is not null && string.IsNullOrWhiteSpace(member.Human.Reference))
                && !(member.Human.InlineBody is null);

            var setCount = (hasAgent ? 1 : 0) + (hasUnit ? 1 : 0) + (hasHuman ? 1 : 0);
            if (setCount > 1)
            {
                throw new ManifestParseException(
                    $"unit.members[{i}] declares more than one of 'agent' / 'unit' / 'human'; " +
                    "a single member entry must reference exactly one participant kind " +
                    "(ADR-0046 §1).");
            }

            if (setCount == 0)
            {
                throw new ManifestParseException(
                    $"unit.members[{i}] is missing all of 'agent' / 'unit' / 'human'; " +
                    "every member entry must reference exactly one participant kind " +
                    "by local symbol, 32-char no-dash hex Guid, or inline body " +
                    "(ADR-0046 §1).");
            }

            if (hasAgent || hasUnit)
            {
                // Agent / unit member symbols are addressable peer artefacts
                // — they must be unique within the unit's member list so the
                // installer can resolve each reference to a single peer.
                var symbol = (member.AgentName ?? member.UnitName)!.Trim();
                if (!seenAgentUnitSymbols.Add(symbol))
                {
                    throw new ManifestParseException(
                        $"unit.members lists '{symbol}' more than once. " +
                        "Each member symbol must be unique within a unit's member list.");
                }
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the inline-or-reference value carries no
    /// meaningful symbol — a null/whitespace reference, or an inline body
    /// whose <c>name:</c> field could not be derived (<see cref="InlineArtefactDefinition.InlineName"/>
    /// returns the <c>&lt;inline&gt;</c> placeholder).
    /// </summary>
    private static bool IsBlankScalar(InlineArtefactDefinition def)
    {
        if (def.Reference is not null)
        {
            return string.IsNullOrWhiteSpace(def.Reference);
        }
        // Inline body — the converter falls back to "<inline>" when neither
        // id nor name is set. Treat the placeholder as "no name declared".
        return string.IsNullOrWhiteSpace(def.InlineName)
            || string.Equals(def.InlineName, "<inline>", System.StringComparison.Ordinal);
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
    /// Parses a <c>kind: AgentTemplate</c> YAML document (ADR-0043 §5) into
    /// an <see cref="AgentTemplateManifest"/>. Strict parsing — unknown
    /// fields are a parse error (issue #2406).
    /// </summary>
    public static AgentTemplateManifest ParseAgentTemplate(string yamlText)
    {
        AgentTemplateManifest? manifest;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new RequirementEntryYamlConverter())
                .Build();
            manifest = deserializer.Deserialize<AgentTemplateManifest>(yamlText);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ManifestParseException($"Invalid YAML: {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new ManifestParseException("Manifest is empty.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ApiVersion))
        {
            throw new ManifestParseException(
                "MissingApiVersion: every artefact YAML declares apiVersion: spring.voyage/v1 (ADR-0037 decision 1).");
        }

        if (string.IsNullOrWhiteSpace(manifest.Kind))
        {
            throw new ManifestParseException(
                "MissingKind: every artefact YAML declares kind: Unit/Agent/Skill/UnitTemplate/AgentTemplate/HumanTemplate (ADR-0037 decision 1).");
        }

        if (!string.Equals(manifest.Kind.Trim(), "AgentTemplate", System.StringComparison.Ordinal))
        {
            throw new ManifestParseException(
                $"AgentTemplate YAML declares kind: '{manifest.Kind}' but expected 'AgentTemplate'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new ManifestParseException(
                "Manifest is missing the required top-level 'name' field.");
        }

        // Issue #2436: strict validation of execution.hosting on the
        // template manifest. See the unit-side branch for the rationale.
        if (manifest.Execution is { Hosting: { } templateHosting }
            && !string.IsNullOrWhiteSpace(templateHosting))
        {
            manifest.Execution.Hosting = NormaliseHostingLiteral(
                templateHosting,
                fieldLocation: "execution.hosting");
        }

        return manifest;
    }

    /// <summary>
    /// Parses an <c>kind: Agent</c> YAML document into an
    /// <see cref="AgentManifest"/>. Strict parsing — unknown fields are a
    /// parse error (issue #2406); the typed parser also rejects unknown
    /// <c>execution.hosting</c> literals at parse time (issue #2436).
    /// </summary>
    public static AgentManifest ParseAgent(string yamlText)
    {
        AgentManifest? manifest;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new RequirementEntryYamlConverter())
                .Build();
            manifest = deserializer.Deserialize<AgentManifest>(yamlText);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ManifestParseException($"Invalid YAML: {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new ManifestParseException("Manifest is empty.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ApiVersion))
        {
            throw new ManifestParseException(
                "MissingApiVersion: every artefact YAML declares apiVersion: spring.voyage/v1 (ADR-0037 decision 1).");
        }

        if (string.IsNullOrWhiteSpace(manifest.Kind))
        {
            throw new ManifestParseException(
                "MissingKind: every artefact YAML declares kind: Unit/Agent/Skill (ADR-0037 decision 1).");
        }

        if (!string.Equals(manifest.Kind.Trim(), "Agent", System.StringComparison.Ordinal))
        {
            throw new ManifestParseException(
                $"Agent YAML declares kind: '{manifest.Kind}' but expected 'Agent'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new ManifestParseException(
                "Manifest is missing the required top-level 'name' field.");
        }

        // Issue #2436: strict validation of execution.hosting on the
        // agent manifest. Closes the silent-fallback gap previously
        // present in DbAgentDefinitionProvider.ParseHosting +
        // AgentEndpoints.ParseHostingMode.
        if (manifest.Execution is { Hosting: { } agentHosting }
            && !string.IsNullOrWhiteSpace(agentHosting))
        {
            manifest.Execution.Hosting = NormaliseHostingLiteral(
                agentHosting,
                fieldLocation: "execution.hosting");
        }

        return manifest;
    }

    /// <summary>
    /// Parses a <c>kind: UnitTemplate</c> YAML document (ADR-0043 §5) into
    /// a <see cref="UnitTemplateManifest"/>. Strict parsing — unknown
    /// fields are a parse error (issue #2406).
    /// </summary>
    public static UnitTemplateManifest ParseUnitTemplate(string yamlText)
    {
        // ADR-0046 §1: the legacy `humans:` block is also rejected on
        // template documents — the migration hint is the same as on
        // concrete units.
        RejectLegacyHumansBlock(yamlText);

        UnitTemplateManifest? manifest;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new RequirementEntryYamlConverter())
                .WithTypeConverter(new InlineArtefactDefinitionYamlConverter())
                .Build();
            manifest = deserializer.Deserialize<UnitTemplateManifest>(yamlText);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ManifestParseException($"Invalid YAML: {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new ManifestParseException("Manifest is empty.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ApiVersion))
        {
            throw new ManifestParseException(
                "MissingApiVersion: every artefact YAML declares apiVersion: spring.voyage/v1 (ADR-0037 decision 1).");
        }

        if (string.IsNullOrWhiteSpace(manifest.Kind))
        {
            throw new ManifestParseException(
                "MissingKind: every artefact YAML declares kind: Unit/Agent/Skill/UnitTemplate/AgentTemplate/HumanTemplate (ADR-0037 decision 1).");
        }

        if (!string.Equals(manifest.Kind.Trim(), "UnitTemplate", System.StringComparison.Ordinal))
        {
            throw new ManifestParseException(
                $"UnitTemplate YAML declares kind: '{manifest.Kind}' but expected 'UnitTemplate'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new ManifestParseException(
                "Manifest is missing the required top-level 'name' field.");
        }

        // Issue #2436: strict validation of execution.hosting on the
        // unit-template manifest. UnitTemplate carries the same
        // ExecutionManifest shape as a concrete Unit so the same rule
        // applies — agents stamped from a unit-template's members
        // inherit through the same precedence chain.
        if (manifest.Execution is { Hosting: { } unitTemplateHosting }
            && !string.IsNullOrWhiteSpace(unitTemplateHosting))
        {
            manifest.Execution.Hosting = NormaliseHostingLiteral(
                unitTemplateHosting,
                fieldLocation: "execution.hosting");
        }

        return manifest;
    }

    /// <summary>
    /// Parses a <c>kind: HumanTemplate</c> YAML document (ADR-0046 §4) into a
    /// <see cref="HumanTemplateManifest"/>. Strict parsing — unknown fields
    /// are a parse error.
    /// </summary>
    public static HumanTemplateManifest ParseHumanTemplate(string yamlText)
    {
        HumanTemplateManifest? manifest;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new RequirementEntryYamlConverter())
                .Build();
            manifest = deserializer.Deserialize<HumanTemplateManifest>(yamlText);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ManifestParseException($"Invalid YAML: {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new ManifestParseException("Manifest is empty.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ApiVersion))
        {
            throw new ManifestParseException(
                "MissingApiVersion: every artefact YAML declares apiVersion: spring.voyage/v1 (ADR-0037 decision 1).");
        }

        if (string.IsNullOrWhiteSpace(manifest.Kind))
        {
            throw new ManifestParseException(
                "MissingKind: every artefact YAML declares kind: Unit/Agent/Skill/UnitTemplate/AgentTemplate/HumanTemplate (ADR-0037 decision 1).");
        }

        if (!string.Equals(manifest.Kind.Trim(), "HumanTemplate", System.StringComparison.Ordinal))
        {
            throw new ManifestParseException(
                $"HumanTemplate YAML declares kind: '{manifest.Kind}' but expected 'HumanTemplate'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new ManifestParseException(
                "Manifest is missing the required top-level 'name' field.");
        }

        return manifest;
    }

    /// <summary>
    /// ADR-0046 §1: the legacy top-level <c>humans:</c> block is gone; each
    /// participant is declared under <c>members:</c> with a <c>- human:</c>
    /// entry. The strict typed parser already rejects the unknown field, but
    /// catching it here surfaces a structured error with an actionable
    /// migration hint instead of YamlDotNet's generic "property not found"
    /// message.
    /// </summary>
    private static void RejectLegacyHumansBlock(string yamlText)
    {
        // Pre-scan with a permissive deserializer so the legacy-detection
        // branch never depends on the strict UnitManifest field set.
        var probe = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        Dictionary<string, object?>? rootMap;
        try
        {
            rootMap = probe.Deserialize<Dictionary<string, object?>>(yamlText);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // Malformed YAML — let the typed parser produce the precise
            // diagnostic; nothing useful to say at this layer.
            return;
        }

        if (rootMap is not null && rootMap.ContainsKey("humans"))
        {
            throw new ManifestParseException(
                "LegacyHumansBlock: `humans:` block is no longer a top-level slot; " +
                "use `members: [{ human: { roles: [...] } }]` (ADR-0046 §1). " +
                "Each entry under `members:` carries one of `agent:` / `unit:` / `human:` " +
                "as the participant discriminator.");
        }
    }

    /// <summary>
    /// Returns the list of <see cref="UnsupportedSections"/> that are actually
    /// populated on <paramref name="manifest"/>.
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
        "requires" => manifest.Requires is { Count: > 0 },
        "policies" => manifest.Policies is { Count: > 0 },
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
