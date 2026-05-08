// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;
using System.IO;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Parses a per-artefact unit YAML document (ADR-0037 decision 1) into a
/// <see cref="UnitManifest"/>. Each unit YAML is a kind-discriminated
/// top-level document (<c>apiVersion: spring.voyage/v1</c>,
/// <c>kind: Unit</c>, <c>name</c>, <c>description</c>, …) with no
/// wrapping <c>unit:</c> key.
/// </summary>
public static class ManifestParser
{
    /// <summary>
    /// Sections of the unit manifest grammar that are parsed but not yet
    /// applied by the platform.
    /// </summary>
    public static readonly IReadOnlyList<string> UnsupportedSections = new[]
    {
        "ai", "requires", "policies", "humans",
    };

    /// <summary>
    /// Parses the manifest YAML text into a <see cref="UnitManifest"/>.
    /// Throws <see cref="ManifestParseException"/> if the document is
    /// malformed, the required header fields are missing, or the document
    /// is in the pre-ADR-0037 wrapped shape.
    /// </summary>
    public static UnitManifest Parse(string yamlText)
    {
        // Detect legacy ai-block shapes (ai.agent / ai.model-as-string)
        // by walking the raw YAML stream BEFORE typed deserialisation —
        // YamlDotNet's typed binder silently drops unmatched keys, and
        // an `ai.model:` scalar where we expect a mapping would surface
        // as a confusing parse error. Doing this first lets us emit a
        // precise ADR-0038 migration hint instead.
        DetectLegacyAiShapes(yamlText);

        // ADR-0039 § 9: `execution.containerRuntime` is removed — the
        // container runtime is platform configuration, not a per-unit
        // / per-agent field. A unit YAML still carrying the key is
        // rejected here with a precise migration hint (matches the
        // wire-DTO `LegacyContainerRuntimeField` error path so operators
        // see the same message regardless of entry point).
        DetectLegacyContainerRuntime(yamlText);

        UnitManifest? manifest;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new RequirementEntryYamlConverter())
                .IgnoreUnmatchedProperties()
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

        // ADR-0037 decision 6: detect legacy wrapper / structure / connectors
        // and surface a precise migration hint.
        if (manifest.LegacyUnitWrapper is not null)
        {
            throw new ManifestParseException(
                "LegacyArtefactWrapper: unit YAML wraps the body in a 'unit:' key. " +
                "ADR-0037 decision 1 — drop the wrapping 'unit:' key; hoist the body to the " +
                "top level with apiVersion: spring.voyage/v1, kind: Unit, name, description.");
        }

        if (manifest.LegacyStructure is not null)
        {
            throw new ManifestParseException(
                "LegacyStructureField: 'structure:' is removed in ADR-0037 decision 1; " +
                "the membership graph already encodes the structure.");
        }

        if (manifest.LegacyConnectors is not null)
        {
            throw new ManifestParseException(
                "LegacyUnitConnectorsField: unit-level 'connectors:' is renamed to 'requires:' in ADR-0037 decision 3. " +
                "Each entry is a single-key mapping ('- connector: <slug>').");
        }

        if (manifest.Execution is { LegacyTool: { } legacyTool } && !string.IsNullOrWhiteSpace(legacyTool))
        {
            throw new ManifestParseException(
                "LegacyExecutionToolField: 'execution.tool:' is removed in #1732. " +
                "The execution tool is now derived from the runtime registry via 'ai.runtime:' " +
                "(ADR-0038). Drop 'execution.tool:' and ensure 'ai.runtime:' names a registered " +
                "agent runtime (e.g. 'claude-code', 'codex', 'gemini', 'spring-voyage').");
        }

        if (manifest.Execution is { LegacyProvider: { } legacyExecProvider } && !string.IsNullOrWhiteSpace(legacyExecProvider))
        {
            throw new ManifestParseException(
                "LegacyExecutionProviderField: 'execution.provider:' is removed in ADR-0038. " +
                "The provider is intrinsic to 'ai.model.provider'. Drop 'execution.provider:' " +
                "and declare the provider on the structured model selector " +
                "(e.g. 'ai.model: { provider: anthropic, id: claude-opus-4-7 }').");
        }

        if (string.IsNullOrWhiteSpace(manifest.ApiVersion))
        {
            throw new ManifestParseException(
                "MissingApiVersion: every artefact YAML declares apiVersion: spring.voyage/v1 (ADR-0037 decision 1).");
        }

        if (string.IsNullOrWhiteSpace(manifest.Kind))
        {
            throw new ManifestParseException(
                "MissingKind: every artefact YAML declares kind: Unit/Agent/Skill/Workflow (ADR-0037 decision 1).");
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

        return manifest;
    }

    /// <summary>
    /// Validates the <c>members:</c> list against the v0.1 manifest grammar.
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
        "humans" => manifest.Humans is { Count: > 0 },
        _ => false,
    };

    /// <summary>
    /// Walks the raw YAML and rejects pre-ADR-0038 ai-block shapes with
    /// precise migration hints:
    /// <list type="bullet">
    /// <item><description><c>ai.agent</c> → <c>LegacyAiAgentField</c>.</description></item>
    /// <item><description><c>ai.model</c> as a scalar → <c>LegacyAiModelStringForm</c>.</description></item>
    /// </list>
    /// Emitted by both <see cref="ManifestParser.Parse"/> and the agent
    /// manifest path on <c>PackageManifestParser</c> via
    /// <see cref="DetectLegacyAiShapes"/> so unit and agent YAMLs share
    /// one rejection rule.
    /// </summary>
    internal static void DetectLegacyAiShapes(string yamlText)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            return;
        }

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            stream.Load(new StringReader(yamlText));
        }
        catch (YamlException)
        {
            // Typed deserialisation will surface the same parse error
            // with a richer message; bail out and let it run.
            return;
        }

        foreach (var doc in stream.Documents)
        {
            if (doc.RootNode is not YamlMappingNode root)
            {
                continue;
            }

            if (!TryGetMapping(root, "ai", out var aiNode))
            {
                continue;
            }

            if (TryGetScalar(aiNode!, "agent", out _))
            {
                throw new ManifestParseException(
                    "LegacyAiAgentField: 'ai.agent:' is removed in ADR-0038. " +
                    "Use 'ai.runtime:' with a runtime id ('claude-code', 'codex', " +
                    "'gemini', 'spring-voyage', or a future custom runtime declared " +
                    "in platform/runtime-catalog.yaml).");
            }

            if (aiNode!.Children.TryGetValue(new YamlScalarNode("model"), out var modelNode)
                && modelNode is YamlScalarNode)
            {
                throw new ManifestParseException(
                    "LegacyAiModelStringForm: 'ai.model:' is now a structured " +
                    "{provider, id} object in ADR-0038. Replace the scalar with " +
                    "'ai.model: { provider: <provider-id>, id: <model-id> }' " +
                    "(e.g. 'ai.model: { provider: anthropic, id: claude-opus-4-7 }').");
            }
        }
    }

    /// <summary>
    /// Walks the raw YAML and rejects pre-ADR-0039 shapes that declare a
    /// <c>containerRuntime:</c> field — either at the document root or
    /// nested under <c>execution:</c> — with the
    /// <c>LegacyContainerRuntimeField</c> migration hint from ADR-0039
    /// § 9. Shared by <see cref="ManifestParser.Parse"/> and the
    /// package-side parser so unit YAML, agent YAML (via the validator),
    /// and the package-level <c>execution:</c> block all reject the same
    /// shape.
    /// </summary>
    internal static void DetectLegacyContainerRuntime(string yamlText)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            return;
        }

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            stream.Load(new StringReader(yamlText));
        }
        catch (YamlException)
        {
            // Typed deserialisation will surface the same parse error
            // with a richer message; bail out and let it run.
            return;
        }

        foreach (var doc in stream.Documents)
        {
            if (doc.RootNode is not YamlMappingNode root)
            {
                continue;
            }

            // ADR-0039 § 9: reject `containerRuntime:` at the document root
            // (e.g. a wire-DTO body that hoisted the field out of execution:).
            if (root.Children.ContainsKey(new YamlScalarNode("containerRuntime")))
            {
                throw new ManifestParseException(LegacyContainerRuntimeMessage);
            }

            // ADR-0039 § 9: reject `execution.containerRuntime:` — the
            // canonical location of the legacy field on unit / package
            // manifests (per the migration table).
            if (TryGetMapping(root, "execution", out var executionNode)
                && executionNode!.Children.ContainsKey(new YamlScalarNode("containerRuntime")))
            {
                throw new ManifestParseException(LegacyContainerRuntimeMessage);
            }
        }
    }

    /// <summary>
    /// ADR-0039 § 9 migration hint surfaced by both the unit / agent
    /// manifest parser path and the wire-DTO rejection path.
    /// </summary>
    internal const string LegacyContainerRuntimeMessage =
        "LegacyContainerRuntimeField: containerRuntime is removed in ADR-0039; " +
        "the container runtime is platform configuration.";

    private static bool TryGetMapping(YamlMappingNode parent, string key, out YamlMappingNode? value)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlMappingNode mapping)
        {
            value = mapping;
            return true;
        }
        value = null;
        return false;
    }

    private static bool TryGetScalar(YamlMappingNode parent, string key, out string? value)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar)
        {
            value = scalar.Value;
            return true;
        }
        value = null;
        return false;
    }
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