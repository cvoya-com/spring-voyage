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

        // Spring Voyage directory tools (#2231). Exposes sv.get_self,
        // sv.get_member, sv.list_members, sv.get_siblings, sv.get_parents
        // so the agent runtime can navigate the unit / agent composition
        // graph at runtime without baking the directory into the system
        // prompt. Registered via TryAddEnumerable so cloud hosts can add
        // a tenant-aware decorator without displacing the OSS default.
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
