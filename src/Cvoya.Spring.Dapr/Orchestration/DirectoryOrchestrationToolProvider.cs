// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Reflection;
using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;

/// <summary>
/// Default <see cref="IOrchestrationToolProvider"/> backing the v0.1
/// orchestration-tool surface defined in ADR-0039 §3. Returns the closed
/// two-tool set unconditionally for every <c>agent://</c> or <c>unit://</c>
/// address; other schemes return an empty array.
/// </summary>
/// <remarks>
/// <para>
/// The orchestration surface is the two action verbs (<c>delegate_to</c>,
/// <c>fanout_to</c>) only — discovery, inspection, and status queries live
/// on the <c>sv.*</c> directory tool surface exposed by
/// <c>SvDirectorySkillRegistry</c>. The 2026-05-19 ADR-0039 amendment
/// (#2536) removed the membership-attachment gate; the subsequent shrink
/// (#2537) dropped <c>list_members</c> / <c>inspect</c> / <c>query_status</c>
/// because <c>sv.list_members</c> / <c>sv.get_member</c> / <c>sv.get_status</c>
/// already covered them.
/// </para>
/// <para>
/// The descriptors are static — built once from embedded JSON schema
/// resources at startup — so per-call work is O(1). The schemas live next
/// to this file under
/// <c>Orchestration/Resources/&lt;tool-name&gt;.&lt;input|output&gt;.schema.json</c>
/// and are wired in as <c>&lt;EmbeddedResource&gt;</c> entries in
/// <c>Cvoya.Spring.Dapr.csproj</c>.
/// </para>
/// </remarks>
public class DirectoryOrchestrationToolProvider : IOrchestrationToolProvider
{
    private readonly OrchestrationToolDescriptor[] _toolset = LoadStaticToolset();

    /// <inheritdoc />
    public OrchestrationToolDescriptor[] GetOrchestrationTools(Address agent, Guid threadId)
    {
        ArgumentNullException.ThrowIfNull(agent);

        // Schemes outside agent:// and unit:// (e.g. human://, connector://)
        // are not orchestration callers — return empty rather than the
        // closed two-tool set.
        if (!string.Equals(agent.Scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(agent.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<OrchestrationToolDescriptor>();
        }

        return _toolset;
    }

    /// <summary>
    /// Loads the two orchestration-tool descriptors once from embedded
    /// JSON schema resources. The order matches the
    /// <see cref="OrchestrationToolName"/> declaration so the toolset reads
    /// deterministically in logs and tool-call catalogues.
    /// </summary>
    private static OrchestrationToolDescriptor[] LoadStaticToolset()
    {
        var assembly = typeof(DirectoryOrchestrationToolProvider).Assembly;

        return new[]
        {
            BuildDescriptor(assembly, OrchestrationToolName.DelegateTo, "delegate_to"),
            BuildDescriptor(assembly, OrchestrationToolName.FanoutTo, "fanout_to"),
        };
    }

    private static OrchestrationToolDescriptor BuildDescriptor(
        Assembly assembly,
        OrchestrationToolName name,
        string wireName)
    {
        var input = LoadSchema(assembly, $"{wireName}.input.schema.json");
        var output = LoadSchema(assembly, $"{wireName}.output.schema.json");
        return new OrchestrationToolDescriptor(name, input, output);
    }

    private static JsonElement LoadSchema(Assembly assembly, string fileName)
    {
        var resourceName = $"Cvoya.Spring.Dapr.Orchestration.Resources.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded orchestration-tool schema '{resourceName}' was not found. " +
                "Verify the EmbeddedResource entry in Cvoya.Spring.Dapr.csproj.");

        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
