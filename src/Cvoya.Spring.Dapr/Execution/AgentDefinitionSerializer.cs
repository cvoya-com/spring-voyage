// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Serialises agent and tenant configuration into the file shapes the
/// platform writes under <c>/spring/context/</c> (D1 spec § 2.2.2). Pulled
/// out of <see cref="A2AExecutionDispatcher"/> so the
/// <see cref="IAgentBootstrapBundleProvider"/> (ADR-0055) can produce the
/// same byte-exact files without duplicating the formatting rules.
/// </summary>
public interface IAgentDefinitionSerializer
{
    /// <summary>
    /// Returns the agent-definition YAML written to
    /// <c>/spring/context/agent-definition.yaml</c>. Uses
    /// <c>underscore_case</c> field names so the Python SDK's
    /// <c>yaml.safe_load</c> round-trips cleanly with the D1 spec's
    /// example payload.
    /// </summary>
    string SerializeAgentDefinitionYaml(AgentDefinition definition);

    /// <summary>
    /// Returns the minimal tenant-config JSON written to
    /// <c>/spring/context/tenant-config.json</c>. The OSS platform has no
    /// separate tenant-config blob; the tenant id is the only tenant-level
    /// datum available at launch time.
    /// </summary>
    string SerializeTenantConfigJson(Guid tenantId);
}

/// <summary>
/// Default <see cref="IAgentDefinitionSerializer"/>. Singleton — the
/// underlying YamlDotNet serialiser is thread-safe and the runtime
/// catalogue lookup is read-only.
/// </summary>
public sealed class AgentDefinitionSerializer(IRuntimeCatalog runtimeCatalog) : IAgentDefinitionSerializer
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private readonly IRuntimeCatalog _runtimeCatalog = runtimeCatalog
        ?? throw new ArgumentNullException(nameof(runtimeCatalog));

    /// <inheritdoc />
    public string SerializeAgentDefinitionYaml(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // ADR-0038: emit the derived launcher strategy id so containers
        // can see which in-container engine was selected. Sourced from
        // the catalogue's `launcher` field on the runtime entry.
        string? kind = null;
        if (definition.Execution is not null)
        {
            var runtime = _runtimeCatalog.GetAgentRuntime(definition.Execution.Runtime);
            kind = runtime?.Launcher;
        }

        var doc = new
        {
            agent_id = definition.AgentId,
            name = definition.Name,
            instructions = definition.Instructions,
            execution = definition.Execution is null ? null : new
            {
                runtime = definition.Execution.Runtime,
                kind,
                image = definition.Execution.Image,
                hosting = definition.Execution.Hosting.ToString().ToLowerInvariant(),
                model = definition.Execution.Model is null ? null : new
                {
                    provider = definition.Execution.Model.Provider,
                    id = definition.Execution.Model.Id,
                },
                concurrent_threads = definition.Execution.ConcurrentThreads,
            },
        };
        return YamlSerializer.Serialize(doc);
    }

    /// <inheritdoc />
    public string SerializeTenantConfigJson(Guid tenantId)
    {
        return JsonSerializer.Serialize(new { tenant_id = GuidFormatter.Format(tenantId) });
    }
}
