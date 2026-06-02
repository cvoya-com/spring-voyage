// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.AgentSdk;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Sample.ToolsAgent;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end coverage for the platform-side tool-introspection pipeline
/// (#2336 / Sub C of #2332).
/// </summary>
/// <remarks>
/// The fixture stands up:
/// <list type="bullet">
///   <item>A real <c>ToolsEndpointServer</c> bound to an ephemeral port,
///   pre-loaded with the sample <c>acme.*</c> registrations.</item>
///   <item>An in-memory <see cref="SpringDbContext"/> with seed rows on
///   <c>agent_definitions</c> and <c>unit_definitions</c>.</item>
///   <item>An <see cref="HttpAgentToolsIntrospector"/> wired against
///   both, with a direct HttpClient factory that bypasses the
///   container-runtime exec path.</item>
/// </list>
/// Pattern mirrors <see cref="PackageInstallServiceIntegrationTests"/>:
/// in-memory EF, no Testcontainers, deploy/launch is mocked.
/// </remarks>
public sealed class AgentToolsIntrospectionTests : IDisposable
{
    private readonly List<ServiceProvider> _providers = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { /* best effort */ }
        }
        foreach (var p in _providers)
        {
            try { p.Dispose(); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task IntrospectAndPersist_PopulatesAgentImageTools()
    {
        await using var fx = BuildFixtureForAgent(out var agentId);
        using var listener = StartAcmeListener(out var endpoint);

        var introspector = fx.Introspector;
        var tools = await introspector.IntrospectAndPersistAsync(
            agentId, "container-1", endpoint, TestContext.Current.CancellationToken);

        tools.Count.ShouldBe(2);
        tools.Select(t => t.Name).ShouldBe(["acme.echo", "acme.timestamp"]);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == agentId, TestContext.Current.CancellationToken);
        row.ImageTools.HasValue.ShouldBeTrue();
        var persisted = row.ImageTools!.Value;
        persisted.ValueKind.ShouldBe(JsonValueKind.Array);
        persisted.GetArrayLength().ShouldBe(2);
        persisted[0].GetProperty("name").GetString().ShouldBe("acme.echo");
        persisted[1].GetProperty("name").GetString().ShouldBe("acme.timestamp");
    }

    [Fact]
    public async Task IntrospectAndPersist_PopulatesUnitImageTools()
    {
        await using var fx = BuildFixtureForUnit(out var unitId);
        using var listener = StartAcmeListener(out var endpoint);

        await fx.Introspector.IntrospectAndPersistAsync(
            unitId, "container-1", endpoint, TestContext.Current.CancellationToken);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(u => u.Id == unitId, TestContext.Current.CancellationToken);
        row.ImageTools.HasValue.ShouldBeTrue();
        row.ImageTools!.Value.GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task IntrospectAndPersist_AgentRedeploysWithMutatedRegistrations_UpdatesColumn()
    {
        await using var fx = BuildFixtureForAgent(out var agentId);

        // First deploy: full sample registry.
        using (var first = StartListener(out var firstEndpoint, BuildAcmeRegistry()))
        {
            await fx.Introspector.IntrospectAndPersistAsync(
                agentId, "container-1", firstEndpoint, TestContext.Current.CancellationToken);
        }

        // Re-deploy with a different registry (only one tool now).
        var mutated = new ToolRegistry();
        mutated.Register(
            new ToolDefinition("acme.echo", "echo only", JsonDocument.Parse("{}").RootElement.Clone(), string.Empty),
            static (args, _) => Task.FromResult(args));
        using (var second = StartListener(out var secondEndpoint, mutated))
        {
            await fx.Introspector.IntrospectAndPersistAsync(
                agentId, "container-2", secondEndpoint, TestContext.Current.CancellationToken);
        }

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == agentId, TestContext.Current.CancellationToken);
        row.ImageTools!.Value.GetArrayLength().ShouldBe(1);
        row.ImageTools!.Value[0].GetProperty("name").GetString().ShouldBe("acme.echo");
    }

    [Fact]
    public async Task IntrospectAndPersist_EndpointKeepsFailing_PreservesPriorImageToolsAndDoesNotThrow()
    {
        // #3003: a fetch that fails across all retries (here a persistent 500)
        // must NOT overwrite a previously-cached image_tools with an empty
        // array — it preserves the prior value. (Pre-#3003 this persisted [].)
        await using var fx = BuildFixtureForAgent(out var agentId);
        await SeedAgentImageToolsAsync(fx, agentId,
            """[{"name":"prior.tool","description":"kept","inputSchema":{}}]""");
        using var listener = StartFailingListener(out var endpoint);

        var tools = await fx.Introspector.IntrospectAndPersistAsync(
            agentId, "container-1", endpoint, TestContext.Current.CancellationToken);

        // Nothing was fetched, so the call returns empty and does not persist.
        tools.ShouldBeEmpty();

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == agentId, TestContext.Current.CancellationToken);
        // The prior value is intact — not clobbered with [].
        row.ImageTools.HasValue.ShouldBeTrue();
        row.ImageTools!.Value.GetArrayLength().ShouldBe(1);
        row.ImageTools!.Value[0].GetProperty("name").GetString().ShouldBe("prior.tool");
    }

    [Fact]
    public async Task IntrospectAndPersist_ConnectionRefused_PreservesPriorImageTools()
    {
        // #3003 (the literal race): on relaunch the sidecar may not be
        // listening yet, so the first GET is refused. After retries the fetch
        // still fails — and the agent's real tool list must survive untouched.
        await using var fx = BuildFixtureForAgent(out var agentId);
        await SeedAgentImageToolsAsync(fx, agentId,
            """[{"name":"prior.tool","description":"kept","inputSchema":{}}]""");

        // Nothing is listening on this port — every attempt is refused.
        var endpoint = new Uri($"http://localhost:{FreePort()}/");

        var tools = await fx.Introspector.IntrospectAndPersistAsync(
            agentId, "container-1", endpoint, TestContext.Current.CancellationToken);

        tools.ShouldBeEmpty();

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == agentId, TestContext.Current.CancellationToken);
        row.ImageTools!.Value.GetArrayLength().ShouldBe(1);
        row.ImageTools!.Value[0].GetProperty("name").GetString().ShouldBe("prior.tool");
    }

    [Fact]
    public async Task IntrospectAndPersist_SuccessfulEmptyArray_OverwritesPriorImageTools()
    {
        // #3003 boundary: a *successful* fetch that legitimately returns an
        // empty array (a tool-less image) is authoritative and DOES overwrite
        // the cache — only fetch failures are skipped, not empty successes.
        await using var fx = BuildFixtureForAgent(out var agentId);
        await SeedAgentImageToolsAsync(fx, agentId,
            """[{"name":"prior.tool","description":"stale","inputSchema":{}}]""");
        using var listener = StartListener(out var endpoint, new ToolRegistry());

        await fx.Introspector.IntrospectAndPersistAsync(
            agentId, "container-1", endpoint, TestContext.Current.CancellationToken);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == agentId, TestContext.Current.CancellationToken);
        row.ImageTools.HasValue.ShouldBeTrue();
        row.ImageTools!.Value.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task IntrospectAndPersist_TransientFailureThenSuccess_PersistsFetchedTools()
    {
        // #3003: the retry must actually retry — a single transient failure
        // (e.g. the listener not bound for the first request) followed by a
        // success persists the real tool list rather than giving up.
        await using var fx = BuildFixtureForAgent(out var agentId);
        using var listener = StartFlakyListener(out var endpoint, failFirst: 1,
            successJson: """[{"name":"acme.echo","description":"echo","inputSchema":{}}]""");

        var tools = await fx.Introspector.IntrospectAndPersistAsync(
            agentId, "container-1", endpoint, TestContext.Current.CancellationToken);

        tools.Count.ShouldBe(1);
        tools[0].Name.ShouldBe("acme.echo");

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == agentId, TestContext.Current.CancellationToken);
        row.ImageTools!.Value.GetArrayLength().ShouldBe(1);
        row.ImageTools!.Value[0].GetProperty("name").GetString().ShouldBe("acme.echo");
    }

    // ── Fixture / helpers ───────────────────────────────────────────────

    private async Task SeedAgentImageToolsAsync(Fixture fx, Guid agentId, string toolsJson)
    {
        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == agentId);
        using var doc = JsonDocument.Parse(toolsJson);
        row.ImageTools = doc.RootElement.Clone();
        await db.SaveChangesAsync();
    }

    private Fixture BuildFixtureForAgent(out Guid agentId)
    {
        agentId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var fx = BuildFixture();
        using var scope = fx.ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.AgentDefinitions.Add(new AgentDefinitionEntity
        {
            Id = agentId,
            TenantId = tenantId,
            DisplayName = "tools-agent",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return fx;
    }

    private Fixture BuildFixtureForUnit(out Guid unitId)
    {
        unitId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var fx = BuildFixture();
        using var scope = fx.ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.UnitDefinitions.Add(new UnitDefinitionEntity
        {
            Id = unitId,
            TenantId = tenantId,
            DisplayName = "tools-unit",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return fx;
    }

    private Fixture BuildFixture()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var dbName = $"sv-tools-{Guid.NewGuid():N}";
        services.AddDbContext<SpringDbContext>(opts => opts
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

        var sp = services.BuildServiceProvider();
        _providers.Add(sp);

        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var introspector = new HttpAgentToolsIntrospector(
            () => new HttpClient(),
            scopeFactory,
            NullLogger<HttpAgentToolsIntrospector>.Instance);

        return new Fixture
        {
            Provider = sp,
            ScopeFactory = scopeFactory,
            Introspector = introspector,
        };
    }

    private ToolRegistry BuildAcmeRegistry()
    {
        var registry = new ToolRegistry();
        registry.RegisterAcmeTools();
        return registry;
    }

    private IDisposable StartAcmeListener(out Uri endpoint)
        => StartListener(out endpoint, BuildAcmeRegistry());

    private IDisposable StartListener(out Uri endpoint, IToolRegistry registry)
    {
        var port = FreePort();
        var prefix = $"http://localhost:{port}";

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseUrls(
            builder.WebHost, prefix);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapToolsEndpoint(registry);
        app.StartAsync().GetAwaiter().GetResult();

        endpoint = new Uri(prefix + "/");
        var wrapper = new WebApplicationDisposable(app);
        _disposables.Add(wrapper);
        return wrapper;
    }

    private sealed class WebApplicationDisposable : IDisposable
    {
        private readonly Microsoft.AspNetCore.Builder.WebApplication _app;

        public WebApplicationDisposable(Microsoft.AspNetCore.Builder.WebApplication app)
        {
            _app = app;
        }

        public void Dispose()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                _app.StopAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // best-effort
            }
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private IDisposable StartFailingListener(out Uri endpoint)
    {
        var port = FreePort();
        var prefix = $"http://localhost:{port}/";
        var listener = new FailingHttpListener(prefix);
        listener.Start();
        endpoint = new Uri(prefix);
        _disposables.Add(listener);
        return listener;
    }

    private IDisposable StartFlakyListener(out Uri endpoint, int failFirst, string successJson)
    {
        var port = FreePort();
        var prefix = $"http://localhost:{port}/";
        var listener = new FlakyHttpListener(prefix, failFirst, successJson);
        listener.Start();
        endpoint = new Uri(prefix);
        _disposables.Add(listener);
        return listener;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required IServiceScopeFactory ScopeFactory { get; init; }
        public required HttpAgentToolsIntrospector Introspector { get; init; }

        public ValueTask DisposeAsync()
        {
            Provider.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal HTTP listener that returns 500 on every request — exercises
    /// the introspector's fail-quiet path.
    /// </summary>
    private sealed class FailingHttpListener : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public FailingHttpListener(string prefix)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
        }

        public void Start()
        {
            _listener.Start();
            _loop = Task.Run(() => Loop(_cts.Token));
        }

        private async Task Loop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                try
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                }
                catch
                {
                    // best-effort
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            _cts.Dispose();
            ((IDisposable)_listener).Dispose();
        }
    }

    /// <summary>
    /// HTTP listener that returns 503 for the first <c>failFirst</c> requests
    /// then serves <c>successJson</c> with a 200 — exercises the introspector's
    /// bounded retry-with-backoff path (#3003).
    /// </summary>
    private sealed class FlakyHttpListener : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _failFirst;
        private readonly byte[] _successBody;
        private int _requestCount;
        private Task? _loop;

        public FlakyHttpListener(string prefix, int failFirst, string successJson)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _failFirst = failFirst;
            _successBody = System.Text.Encoding.UTF8.GetBytes(successJson);
        }

        public void Start()
        {
            _listener.Start();
            _loop = Task.Run(() => Loop(_cts.Token));
        }

        private async Task Loop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                try
                {
                    var n = Interlocked.Increment(ref _requestCount);
                    if (n <= _failFirst)
                    {
                        ctx.Response.StatusCode = 503;
                        ctx.Response.Close();
                    }
                    else
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = _successBody.Length;
                        await ctx.Response.OutputStream.WriteAsync(_successBody, ct);
                        ctx.Response.Close();
                    }
                }
                catch
                {
                    // best-effort
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            _cts.Dispose();
            ((IDisposable)_listener).Dispose();
        }
    }
}
