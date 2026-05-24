// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Memory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Capabilities;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Memory;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Routing and directory registrations: directory cache, message router,
/// expertise aggregation, boundary stores, connector persistence, and
/// directory-based skills.
/// </summary>
internal static class ServiceCollectionExtensionsRouting
{
    internal static IServiceCollection AddCvoyaSpringRouting(
        this IServiceCollection services)
    {
        // Agents-as-skills surface (#359 — rework of closed #532). The
        // catalog derives the skill surface live from the expertise
        // directory (#487 / #498) rather than from a startup snapshot, so
        // directory mutations (agent gains expertise, unit boundary
        // changes) propagate on the next enumeration. The invoker is the
        // protocol-agnostic seam that skill callers use instead of
        // IMessageRouter directly — the default implementation routes
        // through the bus so the boundary / permission / policy / activity
        // chain runs end-to-end; the future A2A gateway (#539) will slot in
        // here as an alternative implementation. TryAdd* so downstream
        // hosts (test harnesses, tenant-scoped wrappers, #539 gateway) can
        // pre-register their own catalog / invoker and keep it.
        services.TryAddSingleton<IExpertiseSkillCatalog, ExpertiseSkillCatalog>();
        services.TryAddSingleton<ISkillInvoker, MessageRouterSkillInvoker>();
        services.TryAddSingleton<ExpertiseSkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, ExpertiseSkillRegistry>(
            sp => sp.GetRequiredService<ExpertiseSkillRegistry>()));

        // Directory-search meta-skill registry (#542). Advertises
        // `directory/search` alongside the `expertise/*` surface so planners
        // can call it BEFORE any other skill to resolve "I need something
        // that does X" into a concrete slug. Registered via
        // TryAddEnumerable so the cloud host can add its own search registry
        // (e.g. a tenant-scoped variant) without displacing this one.
        services.TryAddSingleton<DirectorySearchSkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, DirectorySearchSkillRegistry>(
            sp => sp.GetRequiredService<DirectorySearchSkillRegistry>()));

        // Spring Voyage directory tools (#2231, extended in #2491).
        // Exposes sv.directory.get_self, sv.directory.get_member, sv.directory.list_members,
        // sv.directory.get_siblings, sv.directory.get_parents, sv.directory.get_status so the agent
        // runtime can navigate the unit / agent composition graph at
        // runtime without baking the directory into the system prompt.
        // Registered via TryAddEnumerable so cloud hosts can add a
        // tenant-aware decorator without displacing the OSS default.
        services.TryAddSingleton<SvDirectorySkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, SvDirectorySkillRegistry>(
            sp => sp.GetRequiredService<SvDirectorySkillRegistry>()));

        // Spring Voyage memory tools (#2342). EF-backed memory store
        // powers the sv.memory_* tool surface that lets agents and
        // units write, recall, and organise their own memory at
        // runtime. The store is owner- and tenant-scoped singleton
        // that creates a fresh DI scope per call (matches
        // UnitConnectorBindingStore's pattern). The skill registry is
        // also a singleton and depends only on the store — no
        // IEnumerable<ISkillRegistry> closure, so the DI cycle warning
        // documented on the connector binding store does not apply.
        services.TryAddSingleton<IMemoryStore, EfMemoryStore>();
        services.TryAddSingleton<SvMemorySkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, SvMemorySkillRegistry>(
            sp => sp.GetRequiredService<SvMemorySkillRegistry>()));

        // Spring Voyage thread-inspection tools (#2683). Exposes
        // sv.thread.list / sv.thread.get / sv.thread.search /
        // sv.thread.participants so a runtime can inspect the threads it
        // participates in — fetch the timeline, search across message
        // bodies, enumerate participants — without baking thread state
        // into the system prompt or shipping the full history on every
        // turn. Singleton with a scope-factory dependency: the read
        // services (IThreadQueryService, IThreadRegistry) live in a fresh
        // DI scope per invocation, matching the SvDirectorySkillRegistry
        // pattern. TryAddEnumerable so cloud hosts can layer a
        // tenant-aware decorator without displacing the OSS default.
        services.TryAddSingleton<SvThreadSkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, SvThreadSkillRegistry>(
            sp => sp.GetRequiredService<SvThreadSkillRegistry>()));

        // Spring Voyage runtime reflection tools (#2493 / #2581). Exposes
        // sv.runtime.report_decision so a runtime can annotate its
        // routing/delegation choice on the activity stream. Progress
        // reporting lives on SvProgressSkillRegistry (sv.progress.report),
        // the canonical name per ADR-0056 §8. Singleton — depends only on
        // the tenant context and the activity-event bus, both singleton-safe.
        services.TryAddSingleton<SvRuntimeSkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, SvRuntimeSkillRegistry>(
            sp => sp.GetRequiredService<SvRuntimeSkillRegistry>()));

        // Spring Voyage messaging tools (ADR-0051). sv.messaging.send /
        // sv.messaging.multicast were previously served by a separate MCP
        // surface under a per-turn callback JWT; ADR-0051 collapses them onto
        // the single platform MCP server as an ISkillRegistry so they pass
        // through the same effective-grant gate (#2379) and unit-policy
        // enforcement (#162) as every other sv.* tool. Because the tools live
        // in the sv namespace, the grant resolver's platform tier surfaces
        // them implicitly for every agent / unit subject — no grant row is
        // required, which is how existing agents keep their messaging tools.
        // Singleton — depends only on the singleton MessagingToolHandlers
        // delivery seam and the tenant context.
        services.TryAddSingleton<SvMessagingSkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, SvMessagingSkillRegistry>(
            sp => sp.GetRequiredService<SvMessagingSkillRegistry>()));

        // Spring Voyage tool-discovery surface (ADR-0056 §6 / #2656). Exposes
        // sv.tools.list_categories + sv.tools.list so a runtime can enumerate
        // its category surface and pull per-category tool definitions on
        // demand instead of paying for every schema on every turn. The
        // registry resolves the registered ISkillRegistry set from a fresh
        // scope on each invocation — registering it via the singleton
        // ISkillRegistry collection would form a constructor-time DI cycle
        // (the registry would need IEnumerable<ISkillRegistry> at build
        // time and is itself a member of that collection).
        services.TryAddSingleton<SvToolsDiscoverySkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, SvToolsDiscoverySkillRegistry>(
            sp => sp.GetRequiredService<SvToolsDiscoverySkillRegistry>()));

        // Spring Voyage progress-reporting surface (ADR-0056 §8 / #2656).
        // sv.progress.report is the canonical progress tool — emits a
        // RuntimeProgress activity (existing event type) so a long-running
        // turn isn't silent until completion. Writes the activity directly
        // via IActivityEventBus and surfaces the optional 0..1 'fraction'
        // detail field the ADR calls out as a first-class argument.
        services.TryAddSingleton<SvProgressSkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, SvProgressSkillRegistry>(
            sp => sp.GetRequiredService<SvProgressSkillRegistry>()));

        // Routing
        services.AddSingleton<DirectoryCache>();
        services.TryAddSingleton<IDirectoryService, DirectoryService>();
        services.TryAddSingleton<IAgentProxyResolver, AgentProxyResolver>();
        services.TryAddSingleton<MessageRouter>();
        services.TryAddSingleton<IMessageRouter>(sp => sp.GetRequiredService<MessageRouter>());

        // Expertise aggregation (#412). TryAdd so the private cloud repo can
        // decorate with tenant filters or a different cache implementation
        // without forking the OSS default. The store reads from the
        // existing agent / unit actor state keys — no new persistence.
        services.TryAddSingleton<IExpertiseStore, ActorBackedExpertiseStore>();

        // Boundary store (#413) — backed by the unit actor's own state.
        services.TryAddSingleton<IUnitBoundaryStore, ActorBackedUnitBoundaryStore>();

        // Connector persistence ports. Connector packages (GitHub, Arxiv,
        // WebSearch, …) consume these abstractions via constructor
        // injection — including from skills that both the API and the
        // Worker host register — so the defaults must live in the shared
        // Dapr module rather than in a host-specific composition root.
        // TryAdd so the private cloud repo can substitute tenant-scoped
        // implementations.
        services.TryAddSingleton<IUnitConnectorConfigStore, UnitActorConnectorConfigStore>();
        services.TryAddSingleton<IUnitConnectorRuntimeStore, UnitActorConnectorRuntimeStore>();

        // #2456 — connector-agnostic "list bindings of this type" seam.
        // GitHub uses it to fan out an inbound webhook payload across
        // every matching binding in the receiving tenant per
        // ADR-0047 §10. Backed by the EF binding store; TryAdd so cloud
        // overlays can substitute tenant-aware variants.
        services.TryAddSingleton<IUnitConnectorBindingLookup, UnitActorConnectorBindingLookup>();

        // ADR-0047 §10 — connector-agnostic cross-tenant fingerprint
        // probe. The connector's binding-create endpoint calls this
        // before inserting the row; a true result triggers
        // GitHubCrossTenantRepoBindingConflict (HTTP 409). Backed by an
        // IgnoreQueryFilters EF query across the whole binding table,
        // restricted to other tenants by an explicit Where clause.
        services.TryAddSingleton<IConnectorBindingCrossTenantProbe, UnitConnectorBindingCrossTenantProbe>();

        // #2359: the unit-start connector dispatcher must register here, not
        // in the API host. UnitActor.TryAutoStartAsync runs in the Worker
        // process — which has no project reference to Cvoya.Spring.Host.Api —
        // so a Worker-side null on this seam silently aborts the post-
        // validation Stopped → Starting → Running sequence and leaves units
        // wedged in Stopped. Registering in the shared Dapr DI ensures every
        // host that pulls in AddCvoyaSpringDapr resolves the real dispatcher.
        services.TryAddSingleton<IUnitConnectorStartDispatcher, UnitConnectorStartDispatcher>();

        // ADR-0040 / #2050: connector binding persistence is now
        // EF-backed (unit_connector_bindings table). The singleton store
        // creates a fresh DI scope per call to resolve the scoped
        // repository — same pattern as IUnitLiveConfigStore.
        services.TryAddSingleton<IUnitConnectorBindingStore, UnitConnectorBindingStore>();

        // #2442: shared binding-walk helper used by the runtime-context
        // and prompt-context resolvers. Both walks share the same
        // direct-vs-inherited semantics, so the helper lives in one place
        // — change "what bindings apply to a subject" in one place and
        // both resolvers move together.
        services.TryAddSingleton<ConnectorBindingWalker>();

        // #2380: per-launch connector runtime-context resolver. Walks the
        // subject's direct + inherited bindings, invokes each connector's
        // IConnectorRuntimeContextContributor, and merges the contributions
        // into the launch spec. Singleton — internal collaborators are
        // resolved either from the singleton graph (binding store, hierarchy
        // resolver) or from a per-call scope (membership repository).
        services.TryAddSingleton<IConnectorRuntimeContextResolver, ConnectorRuntimeContextResolver>();

        // #2442: per-launch connector prompt-context resolver. Walks the
        // same bindings as the runtime-context resolver but invokes each
        // connector's IConnectorPromptContextContributor and returns the
        // ordered list of markdown fragments the prompt assembler renders
        // under the platform-layer "Connector context" subsection.
        // Singleton for lifetime symmetry with IConnectorRuntimeContextResolver
        // — the dispatcher is also a singleton, so the two resolvers it
        // calls must be too. TryAdd so the cloud overlay can substitute a
        // tenant-aware decorator (e.g. one that redacts the rendered
        // fragment per tenant policy) without touching this registration.
        services.TryAddSingleton<IConnectorPromptContextResolver, ConnectorPromptContextResolver>();

        // #2335 Sub B: tool-grant resolver + image-tier seam. The
        // resolver merges the four provenance tiers (platform / connector
        // / image / explicit) into a single flat list with provenance
        // metadata; the empty image reader is overwritten by Sub C
        // (#2336) once the SDK introspection path ships. Both TryAdd so
        // cloud overlays / Sub C take precedence cleanly.
        services.TryAddSingleton<IImageToolsReader, EmptyImageToolsReader>();
        services.TryAddSingleton<IToolGrantResolver, ToolGrantResolver>();

        // Register the base aggregator as a concrete singleton so the
        // boundary decorator can take a typed inner reference. Tests that
        // want the raw (unfiltered) aggregator can resolve the concrete
        // type directly.
        services.TryAddSingleton<ExpertiseAggregator>();

        // Boundary-filtering decorator wraps the base aggregator by default
        // (#413). Registered with TryAdd so the private cloud repo can pre-
        // register its own IExpertiseAggregator (e.g. a tenant-scoped
        // decorator) and keep it — this registration is skipped. Call sites
        // that resolve IExpertiseAggregator get the boundary-aware view for
        // free; call sites that want the raw aggregator resolve the concrete
        // ExpertiseAggregator instead.
        services.TryAddSingleton<IExpertiseAggregator>(sp =>
            new BoundaryFilteringExpertiseAggregator(
                sp.GetRequiredService<ExpertiseAggregator>(),
                sp.GetRequiredService<IUnitBoundaryStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));

        // Expertise directory search (#542). Lexical / full-text default; a
        // private-cloud host or a future OSS follow-up can swap in a
        // Postgres-FTS or embedding-backed implementation by pre-registering
        // an alternative IExpertiseSearch before calling AddCvoyaSpringDapr.
        services.TryAddSingleton<IExpertiseSearch, InMemoryExpertiseSearch>();

        // Seed expertise from persisted AgentDefinition / UnitDefinition YAML
        // on actor activation (#488). TryAdd so a tenant-scoped host can swap
        // in a store-specific reader without forking. The agent/unit actors
        // depend on this via optional resolution so pre-#488 test harnesses
        // that construct actors manually keep working.
        services.TryAddSingleton<IExpertiseSeedProvider, DbExpertiseSeedProvider>();

        return services;
    }
}
