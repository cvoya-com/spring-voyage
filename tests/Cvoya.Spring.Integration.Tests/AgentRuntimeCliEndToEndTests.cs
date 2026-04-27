// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.AgentRuntimes.OpenAI;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.CredentialHealth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors.Client;
using global::Dapr.Client;
using global::Dapr.Workflow;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Phase 4.24 (#698) — end-to-end CLI integration test audit for the
/// V2 refactor (#674). Each scenario below exercises the full pipeline
/// the <c>spring</c> CLI actually walks — CLI verb → Kiota client →
/// HTTP endpoint (<c>/api/v1/agent-runtimes/*</c>,
/// <c>/api/v1/connectors/*</c>) → tenant install service → EF store —
/// via <see cref="WebApplicationFactory{TEntryPoint}"/> over the real
/// <c>Cvoya.Spring.Host.Api</c> composition.
/// <para>
/// <b>Invariants guarded by this file:</b>
/// </para>
/// <list type="bullet">
///   <item><description>Installing an agent runtime on the default tenant surfaces in <c>list</c> + <c>show</c> (#688 / #693).</description></item>
///   <item><description><c>models set/add/remove</c> writes flow to the wizard's model endpoint (#690).</description></item>
///   <item><description><c>validate-credential</c> round-trips 200 → Valid / 401 → Invalid and stamps the credential-health row (#686 / #691).</description></item>
///   <item><description>Connector install + watchdog middleware flip <c>credentials status</c> to Revoked on 403 (#714 / CONVENTIONS.md § 16).</description></item>
///   <item><description><c>verify-baseline</c> passes when the mock container reports the tool binary; fails with a clear error when it doesn't (regression gate for #668).</description></item>
///   <item><description>Default-tenant bootstrap binds every file-system skill bundle to the default tenant (#676 / #687).</description></item>
///   <item><description>Cross-tenant isolation — a secondary tenant does not see the default tenant's installs (#674 § tenancy refactor).</description></item>
/// </list>
/// <para>
/// These tests run as part of the standard <c>test</c> job. They do not
/// shell out to the real <c>spring</c> CLI binary — that would pull in
/// an out-of-process boundary every test has to work around. Instead
/// they hit the HTTP surface the CLI consumes, which exercises the same
/// server-side code path and is deterministic across CI environments.
/// The CLI's own parsing is covered by <c>Cvoya.Spring.Cli.Tests</c>.
/// </para>
/// </summary>
public sealed class AgentRuntimeCliEndToEndTests : IDisposable
{
    private readonly StubHttpHandler _openAiHandler = new();
    private readonly StubAuthConnectorType _stubAuthConnector = new();
    private readonly MutableTenantContext _tenantContext = new("default");
    private readonly E2EFactory _factory;
    private readonly HttpClient _client;

    // Host.Api serialises enums as strings via Program.cs's
    // JsonStringEnumConverter registration — incoming test reads must
    // match or ReadFromJsonAsync throws "cannot convert $.status".
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public AgentRuntimeCliEndToEndTests()
    {
        _factory = new E2EFactory(
            _openAiHandler,
            _stubAuthConnector,
            _tenantContext);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ─── Scenario 1: Install → list → show ──────────────────────────

    /// <summary>
    /// <c>spring agent-runtime install &lt;id&gt;</c> on the default tenant.
    /// Assert the install surfaces in <c>list</c> and <c>show</c> returns
    /// the expected stored config.
    /// </summary>
    [Fact]
    public async Task InstallAgentRuntime_OnDefaultTenant_SurfacesInListAndShow()
    {
        var ct = TestContext.Current.CancellationToken;

        // Install OpenAI with explicit config so we can assert the stored
        // shape round-trips through list + show.
        var install = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/install",
            new AgentRuntimeInstallRequest(
                Models: new[] { "gpt-4o", "gpt-4o-mini" },
                DefaultModel: "gpt-4o",
                BaseUrl: null),
            ct);
        install.StatusCode.ShouldBe(HttpStatusCode.OK);

        // `spring agent-runtime list` — the CLI's ListAgentRuntimesAsync call.
        var list = await _client.GetFromJsonAsync<InstalledAgentRuntimeResponse[]>(
            "/api/v1/tenant/agent-runtimes/installs", JsonOptions, ct);
        list.ShouldNotBeNull();
        list!.ShouldContain(r => r.Id == "openai");

        // `spring agent-runtime show openai`.
        var show = await _client.GetFromJsonAsync<InstalledAgentRuntimeResponse>(
            "/api/v1/tenant/agent-runtimes/installs/openai", JsonOptions, ct);
        show.ShouldNotBeNull();
        show!.Id.ShouldBe("openai");
        show.DefaultModel.ShouldBe("gpt-4o");
        show.Models.ShouldBe(new[] { "gpt-4o", "gpt-4o-mini" });
        show.ToolKind.ShouldBe("dapr-agent");
    }

    // ─── Scenario 2: models set / add / remove → wizard endpoint ───

    /// <summary>
    /// <c>spring agent-runtime models set/add/remove</c> writes the
    /// tenant-scoped model list; the wizard's model endpoint
    /// (<c>GET /api/v1/agent-runtimes/{id}/models</c>) reflects each
    /// change within the same request sequence.
    /// </summary>
    [Fact]
    public async Task ModelsSetAddRemove_PropagatesToWizardModelsEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await _client.PostAsJsonAsync(
            "/api/v1/tenant/agent-runtimes/installs/openai/install",
            new AgentRuntimeInstallRequest(null, null, null), ct);

        // models set — the CLI implements this as PATCH /config with the
        // full replacement list.
        await PatchModelsAsync("openai", new[] { "gpt-4o", "gpt-4o-mini" }, "gpt-4o", null, ct);

        var afterSet = await _client.GetFromJsonAsync<AgentRuntimeModelResponse[]>(
            "/api/v1/tenant/agent-runtimes/installs/openai/models", JsonOptions, ct);
        afterSet.ShouldNotBeNull();
        afterSet!.Select(m => m.Id).ShouldBe(new[] { "gpt-4o", "gpt-4o-mini" });

        // models add — append a model id.
        await PatchModelsAsync("openai", new[] { "gpt-4o", "gpt-4o-mini", "o4-mini" }, "gpt-4o", null, ct);
        var afterAdd = await _client.GetFromJsonAsync<AgentRuntimeModelResponse[]>(
            "/api/v1/tenant/agent-runtimes/installs/openai/models", JsonOptions, ct);
        afterAdd!.Select(m => m.Id).ShouldBe(new[] { "gpt-4o", "gpt-4o-mini", "o4-mini" });

        // models remove — drop the middle entry.
        await PatchModelsAsync("openai", new[] { "gpt-4o", "o4-mini" }, "gpt-4o", null, ct);
        var afterRemove = await _client.GetFromJsonAsync<AgentRuntimeModelResponse[]>(
            "/api/v1/tenant/agent-runtimes/installs/openai/models", JsonOptions, ct);
        afterRemove!.Select(m => m.Id).ShouldBe(new[] { "gpt-4o", "o4-mini" });
    }

    // T-03 (#945) removed Scenarios 3 & 4. The
    // POST /{id}/validate-credential endpoint was deleted when the
    // corresponding IAgentRuntime.ValidateCredentialAsync method was
    // retired in favour of the backend UnitValidationWorkflow probe
    // plan (GetProbeSteps). Credential-health rows for agent runtimes
    // still flow via the watchdog's refresh-models path and connector
    // scenario below.

    // ─── Scenario 5: connector install + watchdog flips to Revoked ─

    /// <summary>
    /// Install a GitHub-shaped mock connector that carries auth; the
    /// <c>/validate-credential</c> endpoint flips the health row to
    /// <c>Invalid</c> when the connector's hook reports 401, and the
    /// watchdog middleware flips it to <c>Revoked</c> when the connector
    /// sees a 403 on a downstream call. Both paths land in the same
    /// credential-health store the CLI reads via <c>credentials status</c>.
    /// </summary>
    [Fact]
    public async Task ConnectorWithAuth_Validate401_Then_Watchdog403_FlipsToRevoked()
    {
        var ct = TestContext.Current.CancellationToken;

        // Install the mock connector so list/validate-credential resolve.
        var install = await _client.PostAsJsonAsync(
            "/api/v1/tenant/connectors/github-mock/install",
            new ConnectorInstallRequest(null), ct);
        install.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Step 1: validate-credential with a 401-shaped result → Invalid.
        _stubAuthConnector.NextValidationResult = new CredentialValidationResult(
            Valid: false,
            ErrorMessage: "mock provider rejected the token",
            Status: CredentialValidationStatus.Invalid);

        var validate = await _client.PostAsJsonAsync(
            "/api/v1/tenant/connectors/github-mock/validate-credential",
            new CredentialValidateRequest("ghp_bad", SecretName: "client-secret"), ct);
        validate.StatusCode.ShouldBe(HttpStatusCode.OK);
        var validateBody = await validate.Content
            .ReadFromJsonAsync<CredentialValidateResponse>(JsonOptions, ct);
        validateBody!.Status.ShouldBe(CredentialHealthStatus.Invalid);

        var healthAfterValidate = await _client.GetFromJsonAsync<CredentialHealthResponse>(
            "/api/v1/tenant/connectors/github-mock/credential-health?secretName=client-secret",
            JsonOptions, ct);
        healthAfterValidate!.Status.ShouldBe(CredentialHealthStatus.Invalid);

        // Step 2: simulate a 403 coming back from the connector's
        // watchdog-wrapped HttpClient. The watchdog records into the same
        // store; CLI `credentials status` should flip to Revoked.
        //
        // We invoke the wired watchdog directly through a named HttpClient
        // the factory exposes — this exercises the same DelegatingHandler
        // the production code puts in front of every authenticating
        // connector client.
        var factory = _factory.Services.GetRequiredService<IHttpClientFactory>();
        var watchdogClient = factory.CreateClient(TestWatchdogClientName);

        var forbidden = await watchdogClient.GetAsync("https://mock.github/watchdog-probe", ct);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var statusAfterWatchdog = await _client.GetFromJsonAsync<CredentialHealthResponse>(
            "/api/v1/tenant/connectors/github-mock/credential-health?secretName=client-secret",
            JsonOptions, ct);
        statusAfterWatchdog!.Status.ShouldBe(CredentialHealthStatus.Revoked);
    }

    // T-03 (#945) removed Scenario 6. The
    // POST /{id}/verify-baseline endpoint was deleted when the
    // corresponding IAgentRuntime.VerifyContainerBaselineAsync method
    // was retired — the tool-presence check now runs in-container as
    // the VerifyingTool step of the UnitValidationWorkflow probe plan
    // (per-unit, not per-runtime).

    // ─── Scenario 7: skill-bundle tenant binding after bootstrap ──

    /// <summary>
    /// Default-tenant bootstrap binds every file-system skill bundle to
    /// the <c>default</c> tenant; the binding is visible via
    /// <see cref="ITenantSkillBundleBindingService.GetAsync"/> after
    /// bootstrap finishes. The CLI's skill-bundle mutation verbs are V2.1
    /// (per <c>AGENTS.md</c>), so the V2 contract is read-only — this
    /// test guards that read-side invariant.
    /// </summary>
    [Fact]
    public async Task DefaultTenantBootstrap_BindsSkillBundleToDefaultTenant()
    {
        var ct = TestContext.Current.CancellationToken;

        var bootstrap = _factory.Services.GetServices<IHostedService>()
            .OfType<Cvoya.Spring.Dapr.Tenancy.DefaultTenantBootstrapService>()
            .Single();
        await bootstrap.StartAsync(ct);

        // Open a scope so the scoped binding service (and its DbContext)
        // resolve against the default tenant context the factory wired.
        await using var scope = _factory.Services.CreateAsyncScope();
        _tenantContext.Set("default");
        var bindings = scope.ServiceProvider.GetRequiredService<ITenantSkillBundleBindingService>();

        var packageBinding = await bindings.GetAsync(E2EFactory.SeededSkillBundleId, ct);
        packageBinding.ShouldNotBeNull();
        packageBinding!.Enabled.ShouldBeTrue();
        packageBinding.TenantId.ShouldBe("default");
        packageBinding.BundleId.ShouldBe(E2EFactory.SeededSkillBundleId);
    }

    // ─── Scenario 8: cross-tenant isolation smoke test ─────────────

    /// <summary>
    /// Tenant A's installs are invisible to tenant B. The test drives
    /// <c>ITenantAgentRuntimeInstallService</c> directly (the surface
    /// <c>spring agent-runtime list</c> walks) with the mutable tenant
    /// context flipped between tenants — this is the OSS equivalent of
    /// the cloud overlay's request-scoped <c>ITenantContext</c>. The
    /// query-filter contract (<see cref="SpringDbContext"/>) is the real
    /// isolation boundary; this test asserts it holds end-to-end.
    /// </summary>
    [Fact]
    public async Task CrossTenantIsolation_InstallOnA_NotVisibleOnB()
    {
        var ct = TestContext.Current.CancellationToken;

        // Install Ollama as tenant 'alpha'.
        _tenantContext.Set("alpha");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var installService = scope.ServiceProvider
                .GetRequiredService<ITenantAgentRuntimeInstallService>();
            await installService.InstallAsync("ollama", config: null, ct);
            var alphaList = await installService.ListAsync(ct);
            alphaList.ShouldContain(r => r.RuntimeId == "ollama");
        }

        // Switch to tenant 'beta' — the install from 'alpha' must not surface.
        _tenantContext.Set("beta");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var installService = scope.ServiceProvider
                .GetRequiredService<ITenantAgentRuntimeInstallService>();
            var betaList = await installService.ListAsync(ct);
            betaList.ShouldNotContain(r => r.RuntimeId == "ollama",
                "tenant 'beta' must not see tenant 'alpha's install row — the EF Core query filter is the load-bearing isolation boundary.");
            var betaDirect = await installService.GetAsync("ollama", ct);
            betaDirect.ShouldBeNull();
        }

        // Flipping back to 'alpha' must still see the install (sanity check
        // that the filter is symmetric, not a hard delete).
        _tenantContext.Set("alpha");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var installService = scope.ServiceProvider
                .GetRequiredService<ITenantAgentRuntimeInstallService>();
            var alphaAgain = await installService.ListAsync(ct);
            alphaAgain.ShouldContain(r => r.RuntimeId == "ollama");
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private async Task PatchModelsAsync(
        string runtimeId,
        IReadOnlyList<string> models,
        string? defaultModel,
        string? baseUrl,
        CancellationToken ct)
    {
        var patch = new HttpRequestMessage(HttpMethod.Patch,
            $"/api/v1/tenant/agent-runtimes/installs/{runtimeId}/config")
        {
            Content = JsonContent.Create(new { Models = models, DefaultModel = defaultModel, BaseUrl = baseUrl }),
        };
        var response = await _client.SendAsync(patch, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Name of the named HttpClient the factory wires with the
    /// credential-health watchdog pointed at the mock connector's slug.
    /// Scenario 5 drives a 403 through this client to exercise the
    /// watchdog code path without relying on any real network egress.
    /// </summary>
    private const string TestWatchdogClientName = "Cvoya.Spring.E2E.GitHubMockWatchdog";

    // ─── Test infrastructure ────────────────────────────────────────

    /// <summary>
    /// Mutable <see cref="ITenantContext"/> so scenario 8 can flip
    /// tenants between calls. OSS production registers
    /// <see cref="Cvoya.Spring.Dapr.Tenancy.ConfiguredTenantContext"/>
    /// (singleton, read-once); swapping in a test-local mutable variant
    /// via <c>TryAddSingleton</c> pre-registration lets us simulate the
    /// cloud overlay's per-request tenant context without pulling in the
    /// private repo.
    /// </summary>
    private sealed class MutableTenantContext : ITenantContext
    {
        private string _tenantId;
        public MutableTenantContext(string initial) { _tenantId = initial; }
        public string CurrentTenantId => _tenantId;
        public void Set(string tenantId) => _tenantId = tenantId;
    }

    /// <summary>
    /// Deterministic <see cref="HttpMessageHandler"/> for the OpenAI
    /// named client — scenarios 3/4 use this to simulate 200/401 from
    /// the provider without touching the network.
    /// </summary>
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.ServiceUnavailable;
        private string _body = "{}";

        public HttpRequestMessage? LastRequest { get; private set; }

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>
    /// Test-only <see cref="IAgentRuntime"/> registered alongside the OSS
    /// runtimes. Retained after T-03 (#945) as a placeholder so the DI
    /// graph still exposes a mock runtime id — scenario 6 (baseline
    /// verification) was removed along with the host-side endpoint.
    /// </summary>
    private sealed class MockBaselineRuntime : IAgentRuntime
    {
        public const string RuntimeId = "mock-baseline-runtime";
        public string Id => RuntimeId;
        public string DisplayName => "Mock Baseline Runtime (test-only)";
        public string ToolKind => "mock-tool";
        public AgentRuntimeCredentialSchema CredentialSchema { get; } =
            new(AgentRuntimeCredentialKind.None, DisplayHint: null);
        public string CredentialSecretName => "";
        public IReadOnlyList<ModelDescriptor> DefaultModels { get; } =
            new[] { new ModelDescriptor("mock-model", "Mock Model", ContextWindow: null) };
        public IReadOnlyList<ProbeStep> GetProbeSteps(AgentRuntimeInstallConfig config, string credential)
            => Array.Empty<ProbeStep>();
        public Task<FetchLiveModelsResult> FetchLiveModelsAsync(
            string credential, CancellationToken cancellationToken = default)
            => Task.FromResult(FetchLiveModelsResult.Unsupported("mock runtime has no live catalog"));
        public bool IsCredentialFormatAccepted(string credential, CredentialDispatchPath dispatchPath) => true;
    }

    /// <summary>
    /// Test-only <see cref="IConnectorType"/> that carries auth (mirrors
    /// the GitHub-connector shape for scenario 5). <see cref="NextValidationResult"/>
    /// lets the test flip the accept-time outcome per call; the slug is
    /// fixed so the test can also drive the watchdog by recording writes
    /// against the same <c>subjectId</c>.
    /// </summary>
    private sealed class StubAuthConnectorType : IConnectorType
    {
        public const string FixedSlug = "github-mock";
        public Guid TypeId => new("12345678-0000-0000-0000-000000000698");
        public string Slug => FixedSlug;
        public string DisplayName => "GitHub (mock, test-only)";
        public string Description => "Test-only connector that mimics the GitHub App auth shape.";
        public Type ConfigType => typeof(object);
        public CredentialValidationResult? NextValidationResult { get; set; }

        public Task<JsonElement?> GetConfigSchemaAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<JsonElement?>(null);
        public Task OnUnitStartingAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task OnUnitStoppingAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<CredentialValidationResult?> ValidateCredentialAsync(
            string credential, CancellationToken cancellationToken = default)
            => Task.FromResult(NextValidationResult);
        public void MapRoutes(IEndpointRouteBuilder group) { /* no connector-owned routes for the mock. */ }
    }

    /// <summary>
    /// Stubbed <see cref="HttpMessageHandler"/> that always returns 403
    /// — scenario 5 sends a request through the watchdog-wrapped named
    /// client, and the watchdog should record Revoked against the
    /// <see cref="StubAuthConnectorType.FixedSlug"/> subject.
    /// </summary>
    private sealed class AlwaysForbidHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("forbidden"),
                ReasonPhrase = "Forbidden",
            });
    }

    /// <summary>
    /// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> over the
    /// real <c>Cvoya.Spring.Host.Api</c> <c>Program</c>. Swaps out the
    /// Dapr-dependent infrastructure for substitutes, pre-registers the
    /// in-memory EF context, layers the OpenAI stub handler onto the
    /// OpenAI named client, pre-registers the mutable tenant context so
    /// <c>TryAddSingleton</c> inside <c>AddCvoyaSpringDapr</c> respects
    /// it, wires the default-tenant bootstrap (opt-in; not on by default
    /// in Host.Api), and adds a mock <see cref="IAgentRuntime"/> +
    /// <see cref="IConnectorType"/> so scenarios 5 and 6 have first-class
    /// subjects to probe.
    /// </summary>
    private sealed class E2EFactory : WebApplicationFactory<Program>
    {
        public const string SeededSkillBundleId = "e2e-skill-bundle";
        private readonly StubHttpHandler _openAiHandler;
        private readonly StubAuthConnectorType _stubAuthConnector;
        private readonly MutableTenantContext _tenantContext;
        private readonly string _packagesRoot;

        public E2EFactory(
            StubHttpHandler openAiHandler,
            StubAuthConnectorType stubAuthConnector,
            MutableTenantContext tenantContext)
        {
            _openAiHandler = openAiHandler;
            _stubAuthConnector = stubAuthConnector;
            _tenantContext = tenantContext;

            // Plant a single skill-bundle package on disk so the file-system
            // seed provider has something to bind during scenario 7's
            // bootstrap run. Temp dir is cleaned up in Dispose.
            _packagesRoot = Path.Combine(
                Path.GetTempPath(),
                "spring-voyage-e2e-tests",
                $"bundles-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(_packagesRoot, SeededSkillBundleId, "skills"));
            File.WriteAllText(
                Path.Combine(_packagesRoot, SeededSkillBundleId, "skills", "probe.md"),
                "## probe skill");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Identical local-dev / connection-string / ephemeral-key
            // prerequisites to the sibling Host.Api.Tests factory (see the
            // #261/#616/#639 comments there) — the OSS startup validator
            // fires before our in-memory DbContext replacement lands.
            builder.UseSetting("LocalDev", "true");
            builder.UseSetting("ConnectionStrings:SpringDb",
                "Host=test;Database=test;Username=test;Password=test");
            builder.UseSetting("Secrets:AllowEphemeralDevKey", "true");
            builder.UseSetting("Skills:PackagesRoot", _packagesRoot);

            builder.ConfigureServices(services =>
            {
                // Pre-register the mutable tenant context so
                // AddCvoyaSpringDapr's TryAddSingleton respects it —
                // scenario 8 flips it to simulate cross-tenant traffic.
                services.AddSingleton<ITenantContext>(_tenantContext);

                // Replace the real SpringDbContext with an in-memory
                // database. Strip any EF Core / Npgsql descriptors that
                // AddCvoyaSpringDapr may have introduced by the time this
                // override runs.
                var dbDescriptors = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<SpringDbContext>)
                             || d.ServiceType == typeof(DbContextOptions)
                             || d.ServiceType == typeof(SpringDbContext)
                             || (d.ServiceType.FullName?.StartsWith(
                                    "Microsoft.EntityFrameworkCore.", StringComparison.Ordinal) ?? false)
                             || (d.ServiceType.FullName?.StartsWith(
                                    "Npgsql.", StringComparison.Ordinal) ?? false))
                    .ToList();
                foreach (var d in dbDescriptors)
                {
                    services.Remove(d);
                }
                var dbName = $"E2EDb_{Guid.NewGuid():N}";
                services.AddDbContext<SpringDbContext>(opts => opts.UseInMemoryDatabase(dbName));

                // Swap the Dapr-dependent collaborators for substitutes so
                // the host graph resolves without a sidecar. These mirror
                // the subset that the sibling Host.Api.Tests factory
                // replaces — every type here is an infrastructure seam the
                // tests under this file never exercise directly.
                ReplaceWithSubstitute<IDirectoryService>(services);
                ReplaceWithSubstitute<IActorProxyFactory>(services);
                ReplaceWithSubstitute<IAgentProxyResolver>(services);
                ReplaceWithSubstitute<IStateStore>(services);
                ReplaceWithSubstitute<ICostTracker>(services);
                ReplaceWithSubstitute<IActivityQueryService>(services);
                ReplaceWithSubstitute<IAnalyticsQueryService>(services);
                ReplaceWithSubstitute<IActivityEventBus>(services);
                ReplaceWithSubstitute<IUnitActivityObservable>(services);
                ReplaceWithSubstitute<IUnitContainerLifecycle>(services);
                ReplaceWithSubstitute<IGitHubWebhookRegistrar>(services);
                ReplaceWithSubstitute<IUnitConnectorConfigStore>(services);
                ReplaceWithSubstitute<IUnitConnectorRuntimeStore>(services);
                ReplaceWithSubstitute<IExpertiseSearch>(services);

                // Secret stores — stub with permissive defaults; these
                // paths aren't exercised by the audit scenarios but the
                // endpoints' DI graph wants them resolvable.
                var secretStore = Substitute.For<ISecretStore>();
                secretStore.WriteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromResult(Guid.NewGuid().ToString("N")));
                secretStore.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<string?>(null));
                secretStore.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask);
                ReplaceWithInstance(services, secretStore);

                var policy = Substitute.For<ISecretAccessPolicy>();
                policy.IsAuthorizedAsync(
                        Arg.Any<SecretAccessAction>(),
                        Arg.Any<SecretScope>(),
                        Arg.Any<string>(),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(true));
                ReplaceWithInstance(services, policy);

                // Permission service — allow-all for the audit tests.
                var permDescriptors = services
                    .Where(d => d.ServiceType == typeof(IPermissionService))
                    .ToList();
                foreach (var d in permDescriptors) { services.Remove(d); }
                services.AddSingleton(Substitute.For<IPermissionService>());

                // Register the MessageRouter in the same shape the sibling
                // test factory does. Without this the AgentEndpoints (and
                // neighbours) fail to resolve because the router takes
                // four collaborators, not a single substitute.
                services.AddSingleton(sp =>
                {
                    var lf = sp.GetRequiredService<ILoggerFactory>();
                    var dir = sp.GetRequiredService<IDirectoryService>();
                    var resolver = sp.GetRequiredService<IAgentProxyResolver>();
                    var perm = sp.GetRequiredService<IPermissionService>();
                    return new MessageRouter(dir, resolver, perm, lf);
                });

                // Dapr / workflow plumbing — #568 shutdown workaround.
                services.AddSingleton(Substitute.For<DaprClient>());
                services.AddDaprWorkflow(_ => { });
                services.RemoveDaprWorkflowWorker();

                // Scenario 3/4 — swap the OpenAI named client's primary
                // handler. AddHttpMessageHandler stacks handlers ABOVE the
                // primary, so the watchdog still observes traffic.
                services.AddHttpClient(OpenAiAgentRuntime.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => _openAiHandler);

                // Scenario 5 — a named client wired exactly like a real
                // connector's authenticating client: always-403 primary
                // handler + the credential-health watchdog delegating
                // handler pointed at the mock connector's slug.
                services.AddHttpClient(TestWatchdogClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new AlwaysForbidHandler())
                    .AddCredentialHealthWatchdog(
                        CredentialHealthKind.Connector,
                        subjectId: StubAuthConnectorType.FixedSlug,
                        secretName: "client-secret");

                // Scenario 6 — register the mock runtime alongside the
                // OSS runtimes. IAgentRuntimeRegistry enumerates every
                // IAgentRuntime in DI at construction time, so appending a
                // singleton here suffices.
                services.AddSingleton<IAgentRuntime>(_ => new MockBaselineRuntime());

                // Scenario 5 — register the mock connector alongside the
                // OSS connectors. TryAddEnumerable preserves whatever the
                // OSS side already wired (arxiv, github, websearch).
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IConnectorType>(_stubAuthConnector));

                // Scenario 7 — wire the default-tenant bootstrap hosted
                // service. Host.Api does not run bootstrap by default
                // (the Worker owns it in production); the test invokes it
                // explicitly, and this registration makes the service
                // resolvable and the file-system skill-bundle seed
                // provider discoverable.
                services.AddCvoyaSpringDefaultTenantBootstrap();
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<ITenantSeedProvider, FileSystemSkillBundleSeedProvider>());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_packagesRoot))
            {
                try { Directory.Delete(_packagesRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }

        private static void ReplaceWithSubstitute<TService>(IServiceCollection services)
            where TService : class
        {
            var existing = services.Where(d => d.ServiceType == typeof(TService)).ToList();
            foreach (var d in existing) { services.Remove(d); }
            services.AddSingleton(Substitute.For<TService>());
        }

        private static void ReplaceWithInstance<TService>(IServiceCollection services, TService instance)
            where TService : class
        {
            var existing = services.Where(d => d.ServiceType == typeof(TService)).ToList();
            foreach (var d in existing) { services.Remove(d); }
            services.AddSingleton(instance);
        }
    }
}