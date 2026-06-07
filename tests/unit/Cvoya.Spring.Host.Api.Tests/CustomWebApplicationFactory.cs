// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Reactive.Linq;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors.Client;
using global::Dapr.Client;
using global::Dapr.Workflow;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NSubstitute;

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that replaces Dapr-dependent
/// services with test doubles, allowing integration tests to run without a Dapr sidecar.
/// Uses local dev mode to bypass authentication in tests.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Bounded budget for the deterministic-shutdown <c>StopAsync</c> call
    /// in <see cref="DisposeAsync"/>. Hosted services in this fixture are
    /// stubs / no-ops that stop instantly; the budget exists so a buggy
    /// future hosted-service registration that hangs on shutdown surfaces
    /// as a clear timeout rather than a wedged test process.
    /// </summary>
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Stable id of a <em>second</em> Human "Hat" bound to the OSS operator
    /// <c>TenantUser</c>, seeded by <see cref="TestDataSeeder"/> alongside the
    /// primary operator Hat. Lets integration tests observe the multi-Hat
    /// reconciliation the portal relies on (#2900, surfaced by #2888): the
    /// auth-username Hat that <c>/auth/me</c> resolves is a <em>different</em>
    /// Hat than a thread-participant Hat the same operator also wears, yet
    /// both share the operator's <see cref="OssTenantUserIds.Operator"/> join
    /// key. A single seeded operator Hat — where auth-username == primary ==
    /// sending Hat — cannot model that case, so the multi-Hat defect locus is
    /// structurally unobservable without this second row.
    /// </summary>
    /// <remarks>
    /// The Hat is <em>not</em> pinned as <c>PrimaryHumanId</c>, so the
    /// outbound sending-Hat resolver (<c>TenantUserHumanResolver</c>) keeps
    /// selecting the primary <c>test-operator</c> Hat for sends that carry no
    /// thread context — adding this row does not perturb existing message-send
    /// tests. The username is unique, so the auth-username resolve (<c>/me</c>)
    /// mints/looks-up its own distinct <c>local-dev-user</c> Hat rather than
    /// colliding with this one.
    /// </remarks>
    public static readonly Guid OperatorThreadParticipantHumanId =
        new("29000000-0000-0000-0000-000000002900");

    /// <summary>
    /// Captured reference to the in-process <see cref="IHost"/>
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> built for this
    /// fixture. We hold it so <see cref="DisposeAsync"/> can call
    /// <see cref="IHost.StopAsync"/> on it deterministically, before the
    /// base type disposes the wrapped service provider.
    /// </summary>
    /// <remarks>
    /// #2662: without this, <c>Program.Main</c>'s <c>WaitForShutdownAsync</c>
    /// (running inside <c>Microsoft.Extensions.HostFactoryResolver</c>'s
    /// intercepted host pipeline) races the test fixture's teardown — the
    /// resolver triggers <see cref="IServiceProvider"/> disposal before
    /// Main's wait on <see cref="IHostApplicationLifetime"/> returns, and
    /// the wait throws <see cref="ObjectDisposedException"/> with
    /// <c>ObjectName == "IServiceProvider"</c>. Sequencing
    /// <see cref="IHost.StopAsync"/> before any disposal makes the shutdown
    /// signal fire <em>first</em>, so Main's wait unblocks and returns
    /// cleanly before the SP goes away.
    /// </remarks>
    private IHost? _capturedHost;

    /// <summary>
    /// Gets the mock <see cref="IDirectoryService"/> registered in the test DI container.
    /// </summary>
    public IDirectoryService DirectoryService { get; } = Substitute.For<IDirectoryService>();

    /// <summary>
    /// Gets the mock <see cref="IActorProxyFactory"/> registered in the test DI container.
    /// </summary>
    public IActorProxyFactory ActorProxyFactory { get; } = Substitute.For<IActorProxyFactory>();

    /// <summary>
    /// Gets the mock <see cref="IAgentProxyResolver"/> registered in the test DI container.
    /// </summary>
    public IAgentProxyResolver AgentProxyResolver { get; } = Substitute.For<IAgentProxyResolver>();

    /// <summary>
    /// Gets the mock <see cref="IActivityQueryService"/> registered in the test DI container.
    /// </summary>
    public IActivityQueryService ActivityQueryService { get; } = Substitute.For<IActivityQueryService>();

    /// <summary>
    /// Gets the mock <see cref="IAnalyticsQueryService"/> registered in the test DI container.
    /// Tests that exercise <c>/api/v1/analytics/*</c> arrange responses on this mock.
    /// </summary>
    public IAnalyticsQueryService AnalyticsQueryService { get; } = Substitute.For<IAnalyticsQueryService>();

    /// <summary>
    /// Gets the mock <see cref="IActivityEventBus"/> registered in the test
    /// DI container. The substitute's <see cref="IActivityObservable.ActivityStream"/>
    /// is pre-configured to <see cref="Observable.Never{T}"/> so hosted Rx
    /// subscribers (e.g. <c>LabelRoutingRoundtripSubscriber</c>) never observe
    /// the auto-subbed-observable artefact that would otherwise emit
    /// <c>OnError</c> during teardown. Tests that exercise the stream override
    /// the return explicitly.
    /// </summary>
    public IActivityEventBus ActivityEventBus { get; } = CreateStubActivityEventBus();

    /// <summary>
    /// Gets the mock <see cref="IUnitActivityObservable"/> registered in the test DI container.
    /// </summary>
    public IUnitActivityObservable UnitActivityObservable { get; } = Substitute.For<IUnitActivityObservable>();

    /// <summary>
    /// Gets the mock <see cref="IStateStore"/> registered in the test DI container.
    /// </summary>
    public IStateStore StateStore { get; } = Substitute.For<IStateStore>();

    /// <summary>
    /// Gets the mock <see cref="Cvoya.Spring.Dapr.Execution.IExecutionHostGateway"/>
    /// registered in the test DI container. ADR-0052 / Wave 3 (#2618, #2627):
    /// the API host delegates persistent-agent deploy / undeploy / scale /
    /// deployment-status / logs and unit-container teardown to the worker over
    /// this gateway instead of resolving the execution singletons in-process.
    /// </summary>
    /// <remarks>
    /// <see cref="Cvoya.Spring.Dapr.Execution.IExecutionHostGateway.GetDeploymentAsync"/>
    /// is stubbed to the canonical "not running" state by default — the agent
    /// detail / runtime-status endpoints read deployment state on every call,
    /// and "no tracked deployment" is the sane baseline most tests want.
    /// Tests that exercise a running deployment override it per-test.
    /// </remarks>
    public Cvoya.Spring.Dapr.Execution.IExecutionHostGateway ExecutionHostGateway { get; }
        = BuildExecutionGatewayStub();

    private static Cvoya.Spring.Dapr.Execution.IExecutionHostGateway BuildExecutionGatewayStub()
    {
        var gateway = Substitute.For<Cvoya.Spring.Dapr.Execution.IExecutionHostGateway>();
        gateway
            .GetDeploymentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                Cvoya.Spring.Dapr.Execution.PersistentAgentDeploymentState.NotRunning(
                    callInfo.ArgAt<string>(0)));
        // #2708: default UndeployAsync to a successful no-op so tests that
        // exercise the unit/agent delete cascade — which now drives
        // UndeployAsync unconditionally — do not need to stub it. Tests
        // that exercise the failure path override per call.
        gateway
            .UndeployAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                Cvoya.Spring.Dapr.Execution.PersistentAgentDeploymentState.NotRunning(
                    callInfo.ArgAt<string>(0)));
        // #2999: default StopAgentContainerAsync (the volume-preserving teardown
        // for resumable stops — unit stop, agent undeploy, scale-to-zero) to a
        // successful no-op too, mirroring UndeployAsync above.
        gateway
            .StopAgentContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                Cvoya.Spring.Dapr.Execution.PersistentAgentDeploymentState.NotRunning(
                    callInfo.ArgAt<string>(0)));
        return gateway;
    }

    // Issue #2456 removed IGitHubWebhookRegistrar — App-level delivery
    // replaces per-repo webhook registration. The test fixture no longer
    // exposes a registrar substitute.

    /// <summary>
    /// Gets the mock <see cref="IUnitConnectorConfigStore"/> registered in
    /// the test DI container. Tests that exercise connector bindings arrange
    /// responses on this mock to control what the generic
    /// <c>/api/v1/units/{id}/connector</c> endpoints see.
    /// </summary>
    public IUnitConnectorConfigStore ConnectorConfigStore { get; } = Substitute.For<IUnitConnectorConfigStore>();

    /// <summary>
    /// Gets the mock <see cref="IUnitConnectorRuntimeStore"/> registered in
    /// the test DI container.
    /// </summary>
    public IUnitConnectorRuntimeStore ConnectorRuntimeStore { get; } = Substitute.For<IUnitConnectorRuntimeStore>();

    /// <summary>
    /// Stub <see cref="IConnectorType"/> with a known type id used to drive
    /// connector-dispatch tests without depending on the real GitHub
    /// implementation. The stub is registered as the single
    /// <see cref="IConnectorType"/> service in the test DI container.
    /// </summary>
    public IConnectorType StubConnectorType { get; } = CreateStubConnector();

    /// <summary>
    /// Gets the substitute <see cref="ISecretStore"/> wired into the test
    /// DI container. Tests that need to observe (or control) store-layer
    /// writes and deletes configure this stub instead of using a real
    /// Dapr-backed store. <c>WriteAsync</c> is pre-configured to return a
    /// fresh opaque GUID on each call so pass-through writes produce a
    /// valid, unique, opaque store key.
    /// </summary>
    public ISecretStore SecretStore { get; } = CreateStubSecretStore();

    /// <summary>
    /// Gets the substitute <see cref="ISecretAccessPolicy"/> wired into
    /// the test DI container. Defaults to allow-all; tests that exercise
    /// the 403 path re-configure it per call.
    /// </summary>
    public ISecretAccessPolicy SecretAccessPolicy { get; } = CreatePermissiveAccessPolicy();

    /// <summary>
    /// Gets the substitute <see cref="IExpertiseSearch"/> wired into the
    /// test DI container (#542). Tests that exercise
    /// <c>POST /api/v1/directory/search</c> arrange responses on this mock.
    /// </summary>
    public IExpertiseSearch ExpertiseSearch { get; } = Substitute.For<IExpertiseSearch>();

    /// <summary>
    /// Gets the substitute <see cref="IUnitMembershipTenantGuard"/> wired
    /// into the test DI container (#745). Defaults to allow-all so existing
    /// tests that do not exercise the cross-tenant branches keep passing;
    /// tests that want to assert the guard surface configure the
    /// substitute explicitly.
    /// </summary>
    public IUnitMembershipTenantGuard TenantGuard { get; } = CreatePermissiveTenantGuard();

    /// <summary>
    /// Gets the substitute <see cref="IUnitParentInvariantGuard"/> wired
    /// into the test DI container (review feedback on #744). Defaults to
    /// no-op so existing tests that do not exercise the parent-required
    /// removal branches keep passing; tests that want to assert the
    /// guard's 409 surface configure the substitute explicitly.
    /// </summary>
    public IUnitParentInvariantGuard ParentInvariantGuard { get; } = CreatePermissiveParentGuard();

    /// <summary>
    /// Gets the substitute <see cref="IPermissionService"/> wired into
    /// the test DI container. Tests that exercise the unit permission
    /// gate (e.g. the <c>/humans</c> endpoints regressed by #976)
    /// arrange effective-permission responses on this mock.
    /// </summary>
    public IPermissionService PermissionService { get; } = Substitute.For<IPermissionService>();

    /// <summary>
    /// Gets the substitute <see cref="IHatReachabilityService"/> wired into
    /// the test DI container (#2972). It defaults to "every bound Hat
    /// reaches every target" (configured in <see cref="TestDataSeeder"/>),
    /// so endpoint tests that are not about reachability pass unchanged —
    /// the rule itself is covered by the dedicated
    /// <c>HatReachabilityServiceTests</c> unit tests. Tests that exercise
    /// the gate arrange empty / narrowed responses on this mock.
    /// </summary>
    public IHatReachabilityService HatReachability { get; } = Substitute.For<IHatReachabilityService>();

    /// <summary>
    /// Gets the substitute <see cref="IAgentExecutionStore"/> wired into
    /// the test DI container (#1402). Tests that exercise hosting-mode
    /// filtering arrange return values on this mock to control which agents
    /// carry which execution shapes.
    /// </summary>
    public IAgentExecutionStore AgentExecutionStore { get; } = CreateDefaultAgentExecutionStore();

    /// <summary>
    /// Gets the substitute <see cref="IUnitExecutionStore"/> wired into
    /// the test DI container. ADR-0039 G6: tests that exercise the
    /// <c>PUT /api/v1/tenant/units/{id}/execution</c> legacy-rejection
    /// path arrange return values on this mock so the store call short-
    /// circuits without a real Dapr backing.
    /// </summary>
    public IUnitExecutionStore UnitExecutionStore { get; } = CreateDefaultUnitExecutionStore();

    /// <summary>
    /// Gets the substitute <see cref="IInitiativeEngine"/> wired into
    /// the test DI container (#1402). Tests that exercise initiative-level
    /// filtering arrange return values on this mock.
    /// </summary>
    public IInitiativeEngine InitiativeEngine { get; } = CreateDefaultInitiativeEngine();

    /// <summary>
    /// Gets the substitute <see cref="IAgentStateCoordinator"/> wired into
    /// the test DI container. Used by endpoint tests that exercise the
    /// metadata + expertise paths; the legacy unit-keyed skills surface
    /// that used to call this coordinator was retired in #2360.
    /// </summary>
    public IAgentStateCoordinator AgentStateCoordinator { get; } = CreateDefaultAgentStateCoordinator();

    /// <summary>
    /// Gets the substitute <see cref="IToolGrantResolver"/> wired into the
    /// test DI container (#2337 Sub D). The agent + unit Show endpoints
    /// project the resolver's output into the response's
    /// <c>effectiveTools</c> field; tests that need to observe the
    /// projection arrange return values on this stub. Defaults to an
    /// empty list so endpoint tests that do not care about tools keep
    /// passing.
    /// </summary>
    public IToolGrantResolver ToolGrantResolver { get; } = CreateDefaultToolGrantResolver();

    /// <summary>
    /// Gets the substitute <see cref="IUnitSkillBundleStore"/> wired into
    /// the test DI container (#2360). The unit equipped-skills endpoints
    /// (<c>GET/POST/DELETE /api/v1/tenant/units/{id}/skills</c>) call
    /// this store directly; handler-level tests arrange responses on
    /// this stub so they don't need a real state-store backing.
    /// </summary>
    public IUnitSkillBundleStore UnitSkillBundleStore { get; } = CreateDefaultUnitSkillBundleStore();

    /// <summary>
    /// Gets the substitute <see cref="IAgentSkillBundleStore"/> wired
    /// into the test DI container (#2360). Same role as
    /// <see cref="UnitSkillBundleStore"/> but for the agent-subject
    /// surface.
    /// </summary>
    public IAgentSkillBundleStore AgentSkillBundleStore { get; } = CreateDefaultAgentSkillBundleStore();

    private static IAgentExecutionStore CreateDefaultAgentExecutionStore()
    {
        // Default: every agent has no execution shape (hosting mode = null).
        var stub = Substitute.For<IAgentExecutionStore>();
        stub.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentExecutionShape?>(null));
        return stub;
    }

    private static IUnitExecutionStore CreateDefaultUnitExecutionStore()
    {
        // Default: every unit has no persisted execution defaults.
        var stub = Substitute.For<IUnitExecutionStore>();
        stub.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(null));
        return stub;
    }

    private static IAgentStateCoordinator CreateDefaultAgentStateCoordinator()
    {
        // Default: returns a substitute with no per-call arrangements.
        // Tests that need richer state override per-call. The legacy
        // skill (string[]) Get/Set methods were retired in #2360; the
        // coordinator now covers metadata + expertise only.
        return Substitute.For<IAgentStateCoordinator>();
    }

    private static IInitiativeEngine CreateDefaultInitiativeEngine()
    {
        // Default: every agent has initiative level Passive.
        var stub = Substitute.For<IInitiativeEngine>();
        stub.GetCurrentLevelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(InitiativeLevel.Passive));
        return stub;
    }

    private static IToolGrantResolver CreateDefaultToolGrantResolver()
    {
        // Default: every subject has no effective tools so endpoint tests
        // that don't exercise the projection see an empty list.
        var stub = Substitute.For<IToolGrantResolver>();
        stub.ResolveAsync(Arg.Any<Cvoya.Spring.Core.Messaging.Address>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EffectiveTool>>(Array.Empty<EffectiveTool>()));
        return stub;
    }

    private static IUnitSkillBundleStore CreateDefaultUnitSkillBundleStore()
    {
        // Default: every unit has an empty equipped-skills list.
        var stub = Substitute.For<IUnitSkillBundleStore>();
        stub.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Cvoya.Spring.Core.Skills.SkillBundle>>(
                Array.Empty<Cvoya.Spring.Core.Skills.SkillBundle>()));
        return stub;
    }

    private static IAgentSkillBundleStore CreateDefaultAgentSkillBundleStore()
    {
        // Default: every agent has an empty equipped-skills list.
        var stub = Substitute.For<IAgentSkillBundleStore>();
        stub.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Cvoya.Spring.Core.Skills.SkillBundle>>(
                Array.Empty<Cvoya.Spring.Core.Skills.SkillBundle>()));
        return stub;
    }

    private static IActivityEventBus CreateStubActivityEventBus()
    {
        var stub = Substitute.For<IActivityEventBus>();
        // NSubstitute's auto-substituted IObservable<T> property returns a
        // substitute that emits OnError on any subscription, which faulted
        // hosted Rx consumers (LabelRoutingRoundtripSubscriber). Pin the
        // default to a hot observable that never emits so the subscription
        // stays live until the test tears the host down. Tests that need
        // a real stream override this by calling
        // ActivityStream.Returns(subject) inside the test body.
        stub.ActivityStream.Returns(Observable.Never<ActivityEvent>());
        return stub;
    }

    private static IUnitMembershipTenantGuard CreatePermissiveTenantGuard()
    {
        var stub = Substitute.For<IUnitMembershipTenantGuard>();
        stub.ShareTenantAsync(Arg.Any<Cvoya.Spring.Core.Messaging.Address>(),
                              Arg.Any<Cvoya.Spring.Core.Messaging.Address>(),
                              Arg.Any<CancellationToken>())
            .Returns(true);
        stub.EnsureSameTenantAsync(Arg.Any<Cvoya.Spring.Core.Messaging.Address>(),
                                   Arg.Any<Cvoya.Spring.Core.Messaging.Address>(),
                                   Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return stub;
    }

    private static IUnitParentInvariantGuard CreatePermissiveParentGuard()
    {
        var stub = Substitute.For<IUnitParentInvariantGuard>();
        stub.EnsureParentRemainsAsync(
                Arg.Any<Cvoya.Spring.Core.Messaging.Address>(),
                Arg.Any<Cvoya.Spring.Core.Messaging.Address>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return stub;
    }

    private static ISecretStore CreateStubSecretStore()
    {
        var stub = Substitute.For<ISecretStore>();
        // Return a fresh opaque GUID on each WriteAsync so pass-through
        // flows produce unique, opaque store keys — mirroring the real
        // Dapr-backed store's contract.
        stub.WriteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Guid.NewGuid().ToString("N")));
        stub.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        stub.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return stub;
    }

    private static ISecretAccessPolicy CreatePermissiveAccessPolicy()
    {
        var stub = Substitute.For<ISecretAccessPolicy>();
        stub.IsAuthorizedAsync(
                Arg.Any<SecretAccessAction>(),
                Arg.Any<SecretScope>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        return stub;
    }

    private static IConnectorType CreateStubConnector()
    {
        var stub = Substitute.For<IConnectorType>();
        stub.TypeId.Returns(new Guid("00000000-0000-0000-0000-00000000beef"));
        stub.Slug.Returns("stub");
        stub.DisplayName.Returns("Stub");
        stub.Description.Returns("Test-only connector stub");
        stub.ConfigType.Returns(typeof(object));
        stub.GetConfigSchemaAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<System.Text.Json.JsonElement?>(null));
        stub.OnUnitStartingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        stub.OnUnitStoppingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return stub;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Use --local mode to enable LocalDevAuthHandler (bypasses auth).
        builder.UseSetting("LocalDev", "true");

        // Satisfy the AddCvoyaSpringDapr fail-fast connection-string check
        // (#261). AddCvoyaSpringDapr runs inside Program.cs BEFORE
        // ConfigureServices below replaces the DbContext with an in-memory
        // provider, so a missing ConnectionStrings:SpringDb would throw.
        // The value is never opened — the in-memory registration below
        // supersedes it — it just has to be a non-empty string.
        builder.UseSetting("ConnectionStrings:SpringDb",
            "Host=test;Database=test;Username=test;Password=test");

        // Satisfy DispatcherConfigurationRequirement (#2518 — mandatory on
        // every production host; the dispatcher base URL is validated at
        // composition time). The value is never actually called in these
        // tests. The string just has to be a syntactically valid absolute
        // http(s) URL so the validator doesn't trip the malformed-URL branch.
        builder.UseSetting("Dispatcher:BaseUrl", "http://spring-dispatcher.test/");
        builder.UseSetting("Dispatcher:BearerToken", "test-token");

        // Satisfy CallbackBaseUrlConfigurationRequirement (#2597 —
        // mandatory on every production host; the agent-runtime launcher
        // stamps it onto runtime containers as SPRING_CALLBACK_URL). The
        // value is never actually dialled in these tests — it just has to
        // be a syntactically valid absolute http(s) URL so the validator
        // doesn't trip the missing/malformed-URL branches.
        builder.UseSetting("CallbackBaseUrl:BaseUrl", "http://spring-caddy.test:8443/");

        // Satisfy SecretsConfigurationRequirement (#639) — the integration
        // factory never configures a real AES key and the scoped tests
        // don't exercise the encryptor, so the ephemeral dev-key path is
        // the right fit.
        builder.ConfigureServices(services =>
        {
            // Replace the real SpringDbContext with an in-memory database.
            // With #261's fail-fast Npgsql registration we also have to
            // strip the Npgsql-provider descriptors EF Core injected via
            // UseNpgsql — leaving them in place alongside UseInMemoryDatabase
            // triggers EF's "multiple providers registered" guard.
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<SpringDbContext>)
                         || d.ServiceType == typeof(DbContextOptions)
                         || d.ServiceType == typeof(SpringDbContext)
                         || (d.ServiceType.FullName?.StartsWith(
                                "Microsoft.EntityFrameworkCore.", StringComparison.Ordinal) ?? false)
                         || (d.ServiceType.FullName?.StartsWith(
                                "Npgsql.", StringComparison.Ordinal) ?? false))
                .ToList();

            foreach (var descriptor in dbDescriptors)
            {
                services.Remove(descriptor);
            }

            var dbName = $"TestDb_{Guid.NewGuid()}";
            services.AddDbContext<SpringDbContext>(options =>
                options
                    .UseInMemoryDatabase(dbName)
                    .ConfigureWarnings(w =>
                        w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

            // Remove existing registrations that depend on Dapr runtime.
            var typesToRemove = new[]
            {
                typeof(IDirectoryService),
                typeof(MessageRouter),
                typeof(DirectoryCache),
                typeof(IActorProxyFactory),
                typeof(IAgentProxyResolver),
                typeof(IStateStore),
                typeof(IActivityQueryService),
                typeof(IAnalyticsQueryService),
                typeof(IActivityEventBus),
                typeof(IUnitActivityObservable),
                typeof(IUnitConnectorConfigStore),
                typeof(IUnitConnectorRuntimeStore),
                typeof(IConnectorType),
                typeof(ISecretStore),
                typeof(ISecretAccessPolicy),
                typeof(IExpertiseSearch),
                typeof(IUnitMembershipTenantGuard),
                typeof(IUnitParentInvariantGuard),
                // #1402: replace the EF-backed execution store and initiative
                // engine with test doubles so filter contract tests can control
                // which agents carry which execution shapes / initiative levels.
                typeof(IAgentExecutionStore),
                typeof(IInitiativeEngine),
                // ADR-0039 G6: replace the unit-execution store too so the
                // legacy-rejection tests can exercise the PUT path without a
                // real Dapr backing.
                typeof(IUnitExecutionStore),
                // #2285: replace the agent state coordinator so unit-keyed
                // skills endpoint tests can stub skill reads / writes
                // without touching the EF live-config repository.
                typeof(IAgentStateCoordinator),
                // #2337 Sub D: replace the tool-grant resolver so the
                // agent + unit Show endpoint tests can stub the
                // effective-tools projection without exercising the EF
                // connector-binding + grants tables.
                typeof(IToolGrantResolver),
                // #2360: replace the equipped-skill stores so the
                // operator-equip endpoint tests can arrange the bundle
                // list without standing up the state-store backing.
                typeof(IUnitSkillBundleStore),
                typeof(IAgentSkillBundleStore),
            };

            var descriptors = services
                .Where(d => typesToRemove.Contains(d.ServiceType))
                .ToList();

            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            // Re-register with test doubles.
            services.AddSingleton(DirectoryService);
            services.AddSingleton(ActorProxyFactory);
            services.AddSingleton(AgentProxyResolver);
            services.AddSingleton(StateStore);
            services.AddSingleton(ActivityQueryService);
            services.AddSingleton(AnalyticsQueryService);
            services.AddSingleton(ActivityEventBus);
            services.AddSingleton(UnitActivityObservable);
            services.AddSingleton(ExecutionHostGateway);
            services.AddSingleton(ConnectorConfigStore);
            services.AddSingleton(ConnectorRuntimeStore);
            services.AddSingleton(StubConnectorType);
            services.AddSingleton(SecretStore);
            services.AddSingleton(SecretAccessPolicy);
            services.AddSingleton(ExpertiseSearch);
            services.AddSingleton(TenantGuard);
            services.AddSingleton(ParentInvariantGuard);
            services.AddSingleton(AgentExecutionStore);
            services.AddSingleton(UnitExecutionStore);
            services.AddSingleton(InitiativeEngine);
            services.AddSingleton(AgentStateCoordinator);
            services.AddSingleton(ToolGrantResolver);
            services.AddSingleton(UnitSkillBundleStore);
            services.AddSingleton(AgentSkillBundleStore);
            services.AddSingleton(new DirectoryCache());

            // #687: the skill-bundle resolver is now wrapped in a
            // tenant-filtering decorator that requires an enabled binding
            // row before resolving. The integration test host does NOT
            // register the default-tenant bootstrap (Worker owns it in
            // production), so no bindings exist and every bundle resolve
            // would 404. Substitute an allow-all binding service so tests
            // that plant on-disk bundles (e.g. UnitCreationEndpointTests)
            // resolve them transparently — mirrors the post-bootstrap
            // state.
            var bindingDescriptors = services
                .Where(d => d.ServiceType == typeof(ITenantSkillBundleBindingService))
                .ToList();
            foreach (var descriptor in bindingDescriptors)
            {
                services.Remove(descriptor);
            }
            var bindingStub = Substitute.For<ITenantSkillBundleBindingService>();
            bindingStub.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult<TenantSkillBundleBinding?>(
                    new TenantSkillBundleBinding(
                        TenantId: OssTenantIds.Default,
                        BundleId: ci.Arg<string>(),
                        Enabled: true,
                        BoundAt: DateTimeOffset.UtcNow)));
            services.AddSingleton(bindingStub);

            // Remove and re-register permission service.
            var permDescriptors = services
                .Where(d => d.ServiceType == typeof(IPermissionService))
                .ToList();
            foreach (var descriptor in permDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(PermissionService);

            // #2972: replace the real Hat-reachability service with the
            // substitute. Its default (all bound Hats reachable) is wired in
            // TestDataSeeder once the operator's Humans exist.
            var reachDescriptors = services
                .Where(d => d.ServiceType == typeof(IHatReachabilityService))
                .ToList();
            foreach (var descriptor in reachDescriptors)
            {
                services.Remove(descriptor);
            }
            services.AddSingleton(HatReachability);

            // Dapr runtime dependencies.
            services.AddSingleton(Substitute.For<DaprClient>());
            services.AddDaprWorkflow(options => { });

            // Strip the Dapr WorkflowWorker IHostedService via the shared
            // helper. The tests have no sidecar and never exercise workflow
            // execution, so the worker's background gRPC stream only
            // contributes flake on host teardown (see #568 — full root-cause
            // and links live on DaprWorkflowWorkerWorkaround). The
            // DaprWorkflowClient and supporting DI registrations stay so
            // endpoint code that depends on them still resolves.
            services.RemoveDaprWorkflowWorker();

            services.AddSingleton(sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var permSvc = sp.GetRequiredService<IPermissionService>();
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new MessageRouter(DirectoryService, AgentProxyResolver, permSvc, loggerFactory, scopeFactory);
            });
        });

        // ADR-0062: the API host's send endpoints now rewrite the auth
        // principal (tenant-user://) to the speaking-as Hat (human://) at
        // the boundary, walking the FK on humans.tenant_user_id to pick
        // the Human bound to the calling TenantUser. Tests that POST
        // messages need at least one bound Human row + a primary pin on
        // the operator TenantUser so the resolver has something to return.
        // Without this, every Domain send 400s on NoBoundHuman.
        builder.ConfigureServices(services =>
        {
            services.AddHostedService<TestDataSeeder>();
        });
    }

    /// <summary>
    /// Hosted service that seeds the OSS operator <c>TenantUser</c> and a
    /// bound Human row at host start. ADR-0062 § 3 requires a routable
    /// <c>human://</c> sender on every API-side Domain message; the
    /// resolver returns 400 <c>NoBoundHuman</c> when the caller has no
    /// bound Hat. Seeding here keeps every test fixture working without
    /// each test having to plant the rows.
    /// </summary>
    private sealed class TestDataSeeder(IServiceScopeFactory scopeFactory) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var now = DateTimeOffset.UtcNow;

            var existingTu = await db.TenantUsers.FirstOrDefaultAsync(
                u => u.Id == OssTenantUserIds.Operator, cancellationToken);
            if (existingTu is null)
            {
                db.TenantUsers.Add(new Cvoya.Spring.Dapr.Data.Entities.TenantUserEntity
                {
                    Id = OssTenantUserIds.Operator,
                    TenantId = OssTenantIds.Default,
                    AuthSubject = null,
                    DisplayName = "Operator",
                    Description = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await db.SaveChangesAsync(cancellationToken);
            }

            var bound = await db.Humans.FirstOrDefaultAsync(
                h => h.TenantUserId == OssTenantUserIds.Operator, cancellationToken);
            if (bound is null)
            {
                bound = new Cvoya.Spring.Dapr.Data.Entities.HumanEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = OssTenantIds.Default,
                    TenantUserId = OssTenantUserIds.Operator,
                    Username = "test-operator",
                    DisplayName = "Operator",
                    CreatedAt = now,
                };
                db.Humans.Add(bound);
                await db.SaveChangesAsync(cancellationToken);
            }

            var tu = await db.TenantUsers.FirstOrDefaultAsync(
                u => u.Id == OssTenantUserIds.Operator, cancellationToken);
            if (tu is not null && tu.PrimaryHumanId is null)
            {
                tu.PrimaryHumanId = bound.Id;
                tu.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }

            // #2900: seed a SECOND Hat bound to the same operator TenantUser
            // (NOT pinned as primary) so multi-Hat integration tests can
            // observe the auth-username Hat (/me) differing from a
            // thread-participant Hat the operator also wears, while both
            // share the operator join key. See
            // <see cref="OperatorThreadParticipantHumanId"/> for the
            // rationale and why this does not perturb send-path tests.
            var secondHat = await db.Humans.FirstOrDefaultAsync(
                h => h.Id == OperatorThreadParticipantHumanId, cancellationToken);
            if (secondHat is null)
            {
                db.Humans.Add(new Cvoya.Spring.Dapr.Data.Entities.HumanEntity
                {
                    Id = OperatorThreadParticipantHumanId,
                    TenantId = OssTenantIds.Default,
                    TenantUserId = OssTenantUserIds.Operator,
                    Username = "operator-thread-participant",
                    DisplayName = "Operator (thread-participant Hat)",
                    CreatedAt = now,
                });
                await db.SaveChangesAsync(cancellationToken);
            }

            // #2972: default the Hat-reachability substitute to "every bound
            // Hat reaches every target". The bound set is resolved from the
            // DB at call-time so it reflects whatever Humans a test seeded.
            // Endpoint tests that are not about the reachability gate pass
            // unchanged; gate-specific tests override this per-target on the
            // same substitute (the rule itself is covered by the dedicated
            // HatReachabilityServiceTests unit tests).
            var reachability = scope.ServiceProvider.GetRequiredService<IHatReachabilityService>();
            reachability.GetWearableHatsAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<IReadOnlyCollection<Address>>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var tenantUserId = callInfo.ArgAt<Guid>(0);
                    using var inner = scopeFactory.CreateScope();
                    var innerDb = inner.ServiceProvider.GetRequiredService<SpringDbContext>();
                    IReadOnlyList<Guid> ids = innerDb.Humans
                        .Where(h => h.TenantUserId == tenantUserId)
                        .Select(h => h.Id)
                        .ToList();
                    return Task.FromResult(ids);
                });
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// #2662: capture the host so <see cref="DisposeAsync"/> can stop it
    /// before the base type runs its disposal chain. Defer to the base
    /// implementation for actual build / start so the
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> machinery
    /// (server-port wiring, host start) is preserved.
    /// </remarks>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        _capturedHost = base.CreateHost(builder);
        return _capturedHost;
    }

    /// <inheritdoc />
    /// <remarks>
    /// #2662: stop the host explicitly before chaining to the base
    /// disposal. The base's own disposal sequence already calls
    /// <see cref="IHost.StopAsync"/>, but doing it here first guarantees
    /// all hosted services have observed the shutdown signal and
    /// <see cref="Program.Main"/>'s <c>WaitForShutdownAsync</c> has
    /// returned <em>before</em> any service provider is disposed. Without
    /// this sequencing, Main's lifetime-aware wait can resolve
    /// <see cref="IHostApplicationLifetime"/> from a
    /// <see cref="IServiceProvider"/> that the base has already started
    /// disposing — the race the deleted <c>ObjectDisposedException</c>
    /// catch in <c>Program.cs</c> was masking.
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        if (_capturedHost is not null)
        {
            using var cts = new CancellationTokenSource(ShutdownBudget);
            try
            {
                await _capturedHost.StopAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stop budget elapsed — a hosted service is hanging on
                // shutdown. Surface it via the base dispose chain (which
                // logs through the host's logger) rather than masking.
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
