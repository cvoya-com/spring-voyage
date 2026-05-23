// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Worker.Tests;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Host.Worker.Endpoints;

using Microsoft.AspNetCore.Http;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="BootstrapEndpoints.HandleAsync"/> — the
/// worker-hosted bootstrap pull (ADR-0055 §3). Drives the handler with a
/// hand-built <see cref="DefaultHttpContext"/> so the test does not depend
/// on a live Kestrel.
/// </summary>
public class BootstrapEndpointsTests
{
    private const string AgentId = "11111111111111111111111111111111";

    [Fact]
    public async Task Returns200WithBundleAndETagOnValidToken()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        var token = store.Issue(AgentId);
        var bundle = SampleBundle("sha256:aaaa");
        var provider = new StubBundleProvider(AgentId, bundle);
        var ctx = NewContext(token: token);

        await BootstrapEndpoints.HandleAsync(ctx, AgentId, store, provider, TestContext.Current.CancellationToken);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        ctx.Response.Headers.ETag.ToString().ShouldBe("\"sha256:aaaa\"");
        ctx.Response.Headers.CacheControl.ToString().ShouldBe("no-cache");
        ctx.Response.ContentType!.ShouldContain("application/json");

        var body = ReadResponseBody(ctx);
        var roundTrip = JsonSerializer.Deserialize<AgentBootstrapBundle>(body, WebJsonOptions);
        roundTrip.ShouldNotBeNull();
        roundTrip.Version.ShouldBe("sha256:aaaa");
        roundTrip.Files.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Returns304OnIfNoneMatchEqual()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        var token = store.Issue(AgentId);
        var bundle = SampleBundle("sha256:zzzz");
        var provider = new StubBundleProvider(AgentId, bundle);
        var ctx = NewContext(token: token, ifNoneMatch: "\"sha256:zzzz\"");

        await BootstrapEndpoints.HandleAsync(ctx, AgentId, store, provider, TestContext.Current.CancellationToken);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status304NotModified);
        ctx.Response.Headers.ETag.ToString().ShouldBe("\"sha256:zzzz\"");
        ReadResponseBody(ctx).ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Returns200WhenIfNoneMatchDiffers()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        var token = store.Issue(AgentId);
        var bundle = SampleBundle("sha256:current");
        var provider = new StubBundleProvider(AgentId, bundle);
        var ctx = NewContext(token: token, ifNoneMatch: "\"sha256:stale\"");

        await BootstrapEndpoints.HandleAsync(ctx, AgentId, store, provider, TestContext.Current.CancellationToken);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        ctx.Response.Headers.ETag.ToString().ShouldBe("\"sha256:current\"");
    }

    [Fact]
    public async Task Returns401WhenAuthorizationHeaderMissing()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        store.Issue(AgentId);
        var provider = new StubBundleProvider(AgentId, SampleBundle("sha256:x"));
        var ctx = NewContext(token: null);

        await BootstrapEndpoints.HandleAsync(ctx, AgentId, store, provider, TestContext.Current.CancellationToken);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        provider.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Returns401WhenTokenBoundToDifferentAgent()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        var otherToken = store.Issue("99999999999999999999999999999999");
        store.Issue(AgentId);
        var provider = new StubBundleProvider(AgentId, SampleBundle("sha256:x"));
        var ctx = NewContext(token: otherToken);

        await BootstrapEndpoints.HandleAsync(ctx, AgentId, store, provider, TestContext.Current.CancellationToken);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        provider.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Returns401WhenAuthorizationSchemeIsNotBearer()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        var token = store.Issue(AgentId);
        var provider = new StubBundleProvider(AgentId, SampleBundle("sha256:x"));
        var ctx = NewContext(token: null);
        ctx.Request.Headers.Authorization = $"Basic {token}";

        await BootstrapEndpoints.HandleAsync(ctx, AgentId, store, provider, TestContext.Current.CancellationToken);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Returns404WhenAgentUnknown()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        var token = store.Issue(AgentId);
        var provider = new StubBundleProvider(AgentId, bundle: null);
        var ctx = NewContext(token: token);

        await BootstrapEndpoints.HandleAsync(ctx, AgentId, store, provider, TestContext.Current.CancellationToken);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Returns400WhenAgentIdEmpty()
    {
        var store = new InMemoryAgentBootstrapAuthStore();
        var provider = new StubBundleProvider("anything", SampleBundle("sha256:x"));
        var ctx = NewContext(token: "ignored");

        await BootstrapEndpoints.HandleAsync(ctx, agentId: string.Empty, store, provider, TestContext.Current.CancellationToken);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    private static DefaultHttpContext NewContext(string? token, string? ifNoneMatch = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        if (token is not null)
        {
            ctx.Request.Headers.Authorization = $"Bearer {token}";
        }
        if (ifNoneMatch is not null)
        {
            ctx.Request.Headers["If-None-Match"] = ifNoneMatch;
        }
        return ctx;
    }

    private static string ReadResponseBody(HttpContext ctx)
    {
        if (ctx.Response.Body is not MemoryStream ms)
        {
            return string.Empty;
        }
        ms.Position = 0;
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static AgentBootstrapBundle SampleBundle(string version) =>
        new(
            Version: version,
            IssuedAt: new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero),
            Files: new[]
            {
                new AgentBootstrapFile("CLAUDE.md", "sha256:files", "instructions"),
            },
            PlatformFileHashes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CLAUDE.md"] = "sha256:files",
            });

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class StubBundleProvider(string expectedAgentId, AgentBootstrapBundle? bundle)
        : IAgentBootstrapBundleProvider
    {
        public int CallCount { get; private set; }

        public Task<AgentBootstrapBundle?> BuildAsync(string agentId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(agentId == expectedAgentId ? bundle : null);
        }
    }
}
