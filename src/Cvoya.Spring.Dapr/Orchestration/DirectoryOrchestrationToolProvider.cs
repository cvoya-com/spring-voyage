// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Reflection;
using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IOrchestrationToolProvider"/> backing the v0.1
/// orchestration-tool surface defined in ADR-0039 §3. Returns the closed
/// five-tool set when the addressed entity has at least one child in the
/// member graph; returns an empty array otherwise. Address scheme is not a
/// gate.
/// </summary>
/// <remarks>
/// <para>
/// The provider reads the entity's member list via the EF-backed
/// <see cref="IUnitMemberGraphStore"/>. Empty list ⇒ no tools attached;
/// non-empty ⇒ return the cached descriptor set. Whether the address is
/// <c>agent://</c> or <c>unit://</c> is not consulted: an agent that the
/// directory records as having members gets the toolset, and a unit that
/// the directory records as empty does not. Schemes the membership store
/// has no rows for (e.g. <c>human://</c>, <c>connector://</c>) naturally
/// return an empty result.
/// </para>
/// <para>
/// The five descriptors are static — built once from embedded JSON schema
/// resources at startup — so per-call work is bounded by the membership
/// read. The schemas live next to this file under
/// <c>Orchestration/Resources/&lt;tool-name&gt;.&lt;input|output&gt;.schema.json</c>
/// and are wired in as <c>&lt;EmbeddedResource&gt;</c> entries in
/// <c>Cvoya.Spring.Dapr.csproj</c>.
/// </para>
/// <para>
/// The <see cref="IOrchestrationToolProvider.GetOrchestrationTools"/>
/// signature is intentionally synchronous (the launcher consults it on a
/// path that has already resolved every async dependency by the time
/// orchestration tools are computed). The membership read goes through
/// <see cref="IUnitMemberGraphStore"/> — the same EF-backed seam
/// <see cref="Cvoya.Spring.Dapr.Actors.UnitActor"/> uses internally — so this
/// provider does <b>not</b> round-trip through a Dapr actor proxy. Issue
/// #2081: using the actor proxy here caused a re-entrancy deadlock when
/// the provider was invoked from inside a <c>UnitActor</c> turn (Dapr
/// actors are turn-based; a self-call blocks until <c>HttpClient.Timeout</c>
/// fires). The sync-over-async bridge stays as
/// <c>Task.Run(...).GetAwaiter().GetResult()</c> — same pattern used
/// elsewhere in this assembly — but the work it awaits is now a plain EF
/// read that cannot re-enter the actor.
/// </para>
/// </remarks>
public class DirectoryOrchestrationToolProvider : IOrchestrationToolProvider
{
    private readonly IUnitMemberGraphStore _memberGraphStore;
    private readonly ILogger<DirectoryOrchestrationToolProvider> _logger;
    private readonly OrchestrationToolDescriptor[] _toolset;

    public DirectoryOrchestrationToolProvider(
        IUnitMemberGraphStore memberGraphStore,
        ILogger<DirectoryOrchestrationToolProvider> logger)
    {
        _memberGraphStore = memberGraphStore;
        _logger = logger;
        _toolset = LoadStaticToolset();
    }

    /// <inheritdoc />
    public OrchestrationToolDescriptor[] GetOrchestrationTools(Address agent, Guid threadId)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return HasChildren(agent)
            ? _toolset
            : Array.Empty<OrchestrationToolDescriptor>();
    }

    /// <summary>
    /// Reads the entity's persisted member list directly from the EF-backed
    /// <see cref="IUnitMemberGraphStore"/> and reports whether at least
    /// one member is present. Failures (transient EF glitch) degrade to
    /// "no children": attaching tools to an entity whose membership we
    /// could not confirm would surface broken delegation calls to the
    /// runtime, so the conservative answer is to suppress the toolset
    /// until the next launch retry.
    /// </summary>
    private bool HasChildren(Address address)
    {
        try
        {
            // ADR-0039's IOrchestrationToolProvider contract is sync; the
            // store is async. Mirror the Task.Run(...).GetResult() pattern
            // used elsewhere in this assembly to bridge the two without
            // risking a captured-context deadlock. The EF read goes
            // straight to Postgres — no Dapr actor proxy, no re-entrancy
            // risk (#2081).
            var members = Task.Run(() =>
                _memberGraphStore.GetMembersAsync(address.Id)).GetAwaiter().GetResult();

            return members is { Count: > 0 };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read members for {Address} while resolving orchestration tools; suppressing toolset for this launch.",
                address);
            return false;
        }
    }

    /// <summary>
    /// Loads the five orchestration-tool descriptors once from embedded
    /// JSON schema resources. The order matches the
    /// <see cref="OrchestrationToolName"/> declaration so the toolset reads
    /// deterministically in logs and tool-call catalogues.
    /// </summary>
    private static OrchestrationToolDescriptor[] LoadStaticToolset()
    {
        var assembly = typeof(DirectoryOrchestrationToolProvider).Assembly;

        return new[]
        {
            BuildDescriptor(assembly, OrchestrationToolName.ListChildren, "list_children"),
            BuildDescriptor(assembly, OrchestrationToolName.InspectChild, "inspect_child"),
            BuildDescriptor(assembly, OrchestrationToolName.DelegateToChild, "delegate_to_child"),
            BuildDescriptor(assembly, OrchestrationToolName.FanoutToChildren, "fanout_to_children"),
            BuildDescriptor(assembly, OrchestrationToolName.QueryChildStatus, "query_child_status"),
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
