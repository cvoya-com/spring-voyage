// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

using NSubstitute;

using Shouldly;

using Xunit;

public class WebhookEndpointsTests : IClassFixture<WebhookEndpointsTests.Factory>
{
    private const string WebhookSecret = "test-webhook-secret";
    private const string TargetUnitPath = "engineering-team";

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public WebhookEndpointsTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PostGitHubWebhook_MissingEventHeader_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        const string payload = """{"action":"opened"}""";
        using var request = BuildRequest(payload);
        request.Headers.Remove("X-GitHub-Event");
        request.Headers.Add("X-Hub-Signature-256", ComputeSignature(payload, WebhookSecret));

        var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostGitHubWebhook_MissingSignatureHeader_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        const string payload = """{"action":"opened"}""";
        using var request = BuildRequest(payload);
        request.Headers.Add("X-GitHub-Event", "issues");

        var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostGitHubWebhook_InvalidSignature_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;

        const string payload = """{"action":"opened"}""";
        using var request = BuildRequest(payload);
        request.Headers.Add("X-GitHub-Event", "issues");
        request.Headers.Add("X-Hub-Signature-256", "sha256=deadbeef");

        var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostGitHubWebhook_ValidSignatureIgnoredEvent_Returns202AndDoesNotRoute()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();

        const string payload = """{"zen":"Anything added dilutes everything else."}""";
        using var request = BuildRequest(payload);
        request.Headers.Add("X-GitHub-Event", "ping");
        request.Headers.Add("X-Hub-Signature-256", ComputeSignature(payload, WebhookSecret));

        var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // "ping" is not a handled event type, so no Message is produced and
        // the MessageRouter (which would call DirectoryService.ResolveAsync) is not invoked.
        await _factory.DirectoryService
            .DidNotReceive()
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostGitHubWebhook_ValidSignatureHandledEvent_Returns202AndRoutesSuccessfully()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();

        // The connector is configured with DefaultTargetUnitPath = "engineering-team",
        // so the translated message is addressed to unit://engineering-team. The
        // directory resolves that to a unit actor, the actor proxy accepts the
        // message, and MessageRouter returns a successful result.
        var expectedAddress = new Address("unit", TargetUnitPath);
        var directoryEntry = new DirectoryEntry(
            expectedAddress,
            "unit-actor-1",
            "Engineering",
            "Team",
            Role: null,
            RegisteredAt: DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == TargetUnitPath), Arg.Any<CancellationToken>())
            .Returns(directoryEntry);

        var unitProxy = Substitute.For<IUnitActor>();
        unitProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(unitProxy);

        // The MessageRouter routes through IAgentProxyResolver for mailbox dispatch.
        _factory.AgentProxyResolver
            .Resolve("unit", Arg.Any<string>())
            .Returns(unitProxy);

        const string payload = """
        {
            "action": "opened",
            "repository": {
                "name": "demo",
                "full_name": "octo/demo",
                "owner": { "login": "octo" }
            },
            "issue": {
                "number": 1,
                "title": "hi",
                "body": "body",
                "labels": []
            }
        }
        """;

        using var request = BuildRequest(payload);
        request.Headers.Add("X-GitHub-Event", "issues");
        request.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        request.Headers.Add("X-Hub-Signature-256", ComputeSignature(payload, WebhookSecret));

        var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // The endpoint produced a unit-addressed message and MessageRouter resolved it
        // through the directory service to the mocked unit actor.
        await _factory.DirectoryService
            .Received()
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == TargetUnitPath),
                Arg.Any<CancellationToken>());

        await unitProxy
            .Received()
            .ReceiveAsync(
                Arg.Is<Message>(m =>
                    m.To.Scheme == "unit"
                    && m.To.Path == TargetUnitPath
                    && m.From.Scheme == "connector"
                    && m.From.Path == "github"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostGitHubWebhook_ConnectorThrows_Returns500()
    {
        var ct = TestContext.Current.CancellationToken;

        // Valid signature, valid JSON, recognized event type — but missing the "action"
        // property required by GitHubWebhookHandler.TranslateIssueEvent, which causes
        // a KeyNotFoundException inside the connector. The endpoint must catch this
        // at its boundary, log it, and surface a 500 (never silently swallow).
        const string payload = """{"not_an_action":"nope"}""";
        using var request = BuildRequest(payload);
        request.Headers.Add("X-GitHub-Event", "issues");
        request.Headers.Add("X-GitHub-Delivery", "delivery-err-1");
        request.Headers.Add("X-Hub-Signature-256", ComputeSignature(payload, WebhookSecret));

        var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    private static HttpRequestMessage BuildRequest(string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/github")
        {
            Content = new StringContent(payload, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    private static string ComputeSignature(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Test factory that configures a known GitHub webhook secret so requests
    /// can be signed with a matching HMAC. Reuses the shared Dapr-free plumbing
    /// from <see cref="CustomWebApplicationFactory"/>.
    /// </summary>
    public sealed class Factory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GitHub:AppId"] = "12345",
                    ["GitHub:PrivateKeyPem"] = "test-key",
                    ["GitHub:WebhookSecret"] = WebhookSecret,
                    ["GitHub:DefaultTargetUnitPath"] = TargetUnitPath,
                });
            });

            base.ConfigureWebHost(builder);
        }
    }
}