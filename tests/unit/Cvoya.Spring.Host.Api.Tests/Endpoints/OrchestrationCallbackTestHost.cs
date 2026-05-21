// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net.Http.Headers;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Endpoints;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

/// <summary>
/// In-memory host for the relocated orchestration callback endpoints
/// (<c>delegate_to</c> / <c>fanout_to</c> + the MCP JSON-RPC handler).
/// </summary>
/// <remarks>
/// The endpoints moved off the dispatcher onto the Dapr-connected API host
/// (#2586); these tests exercise <c>MapOrchestrationCallbackEndpoints</c>
/// directly with a substituted <see cref="IAgentProxyResolver"/> rather than
/// booting the full API host. The test host registers exactly the services
/// the endpoint group resolves — the orchestration handler graph, the
/// callback-token validator, and the #2582 rejection diagnostics — and runs
/// them on an in-memory <see cref="TestServer"/>.
/// </remarks>
public sealed class OrchestrationCallbackTestHost : IDisposable
{
    // Deterministic 256-bit HMAC key the substituted signing-key provider
    // returns for every tenant — the issuer and validator share it.
    private static readonly byte[] SigningKey =
    [
        0x30, 0x9d, 0xe3, 0xf8, 0x4d, 0x02, 0x5d, 0xaf,
        0x76, 0x11, 0xc8, 0x96, 0x4e, 0x61, 0x73, 0x0b,
        0x44, 0x8e, 0x26, 0x74, 0x95, 0xe2, 0xab, 0x19,
        0xda, 0xc4, 0x31, 0x82, 0x07, 0xbd, 0x58, 0x6f,
    ];

    private readonly Dictionary<string, IAgent> _agents = new();
    private readonly ITenantSigningKeyProvider _keyProvider;
    private readonly List<ActivityEvent> _capturedActivities = new();
    private readonly object _capturedLock = new();
    private readonly WebApplication _app;

    public OrchestrationCallbackTestHost()
    {
        _keyProvider = Substitute.For<ITenantSigningKeyProvider>();
        _keyProvider.GetSigningKey(Arg.Any<Guid>()).Returns(SigningKey);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(_keyProvider);
        builder.Services.AddSingleton(Options.Create(new CallbackTokenOptions()));
        builder.Services.AddSingleton<CallbackTokenValidator>();
        builder.Services.AddSingleton<OrchestrationCallbackDiagnostics>();
        builder.Services.AddSingleton(CreateCapturingActivityBus());
        builder.Services.AddSingleton(CreateAgentProxyResolver());
        // ADR-0039 §3 gate 6 — single-tenant resolver is the OSS default.
        builder.Services.AddSingleton<IOrchestrationTenantResolver, SingleTenantOrchestrationTenantResolver>();
        builder.Services.AddSingleton<OrchestrationToolHandlers>();
        // The MCP root handler resolves IOrchestrationToolProvider for its
        // tools/list response.
        builder.Services.AddSingleton<IOrchestrationToolProvider, DirectoryOrchestrationToolProvider>();
        // Tighten the ADR-0049 delivery retry budget so the terminal-failure
        // path exhausts in milliseconds under test.
        builder.Services.Configure<OrchestrationDeliveryOptions>(options =>
        {
            options.MaxAttempts = 3;
            options.Budget = TimeSpan.FromSeconds(2);
            options.InitialBackoff = TimeSpan.FromMilliseconds(1);
        });

        _app = builder.Build();
        _app.MapOrchestrationCallbackEndpoints();
        _app.Start();
    }

    public Guid TenantId { get; } = Guid.Parse("dd55c4ea-8d72-5e43-a9df-88d07af02b69");

    public Guid ThreadId { get; } = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");

    /// <summary>Snapshot of every activity published during the test.</summary>
    public IReadOnlyList<ActivityEvent> CapturedActivities
    {
        get
        {
            lock (_capturedLock)
            {
                return _capturedActivities.ToList();
            }
        }
    }

    public void RegisterAgent(Address address, IAgent agent) =>
        _agents[$"{address.Scheme}:{address.Id:N}"] = agent;

    /// <summary>An HTTP client bound to the in-memory test server, no auth header.</summary>
    public HttpClient CreateClient() => _app.GetTestClient();

    /// <summary>An HTTP client carrying a valid callback token for <paramref name="caller"/>.</summary>
    public HttpClient CreateCallbackClient(Address caller)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueToken(caller));
        return client;
    }

    /// <summary>
    /// Issues a callback token minted an hour in the past, so its 5-minute
    /// lifetime has elapsed and the validator rejects it as
    /// <see cref="CallbackTokenValidationReason.Expired"/>.
    /// </summary>
    public string IssueExpiredToken(Address caller)
    {
        var issuer = new CallbackTokenIssuer(
            _keyProvider,
            Options.Create(new CallbackTokenOptions()),
            new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(-1)));

        return issuer.Issue(new CallbackToken(
            TenantId, caller, ThreadId, Guid.NewGuid(), ExpiresAt: default));
    }

    public void Dispose() => ((IDisposable)_app).Dispose();

    private string IssueToken(Address caller)
    {
        var issuer = new CallbackTokenIssuer(
            _keyProvider,
            Options.Create(new CallbackTokenOptions()));

        return issuer.Issue(new CallbackToken(
            TenantId, caller, ThreadId, Guid.NewGuid(), ExpiresAt: default));
    }

    private IActivityEventBus CreateCapturingActivityBus()
    {
        var bus = Substitute.For<IActivityEventBus>();
        bus.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                lock (_capturedLock)
                {
                    _capturedActivities.Add(ci.ArgAt<ActivityEvent>(0));
                }

                return Task.CompletedTask;
            });
        return bus;
    }

    private IAgentProxyResolver CreateAgentProxyResolver()
    {
        var resolver = Substitute.For<IAgentProxyResolver>();
        resolver.Resolve(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci =>
            {
                var scheme = ci.ArgAt<string>(0);
                var actorId = ci.ArgAt<string>(1);
                return _agents.TryGetValue($"{scheme}:{actorId}", out var agent)
                    ? agent
                    : null;
            });

        return resolver;
    }

    /// <summary>A <see cref="TimeProvider"/> pinned to a fixed instant.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
