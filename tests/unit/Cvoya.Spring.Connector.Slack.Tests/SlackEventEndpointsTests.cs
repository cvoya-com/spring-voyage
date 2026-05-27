// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Connector.Slack;
using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connector.Slack.Commands;
using Cvoya.Spring.Connector.Slack.DependencyInjection;
using Cvoya.Spring.Connector.Slack.Inbound;
using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Connector.Slack.WebApi;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Connectors.Slack;
using Cvoya.Spring.Dapr.Data;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end coverage for the Slack inbound endpoints. Boots a
/// minimal <see cref="WebApplicationFactory{TEntryPoint}"/>-like
/// host with the connector's <c>/events</c> endpoint mounted and
/// verifies the signature-verification + url_verification preamble
/// per Slack's contract.
/// </summary>
public class SlackEventEndpointsTests
{
    private static readonly Guid TestTenantId = new("aaaaaaaa-bbbb-cccc-dddd-000000000001");
    private const string SigningSecret = "test-signing-secret";

    [Fact]
    public async Task Events_UrlVerification_RoundTripsChallenge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await TestHarness.CreateAsync();

        var body = JsonSerializer.Serialize(new
        {
            type = "url_verification",
            team_id = "T-acme",
            challenge = "xyz-handshake",
        });
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sig = Sign(ts, body, SigningSecret);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenant/connectors/slack/events");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add(SlackEventEndpoints.TimestampHeader, ts);
        request.Headers.Add(SlackEventEndpoints.SignatureHeader, sig);

        var response = await harness.HttpClient.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        responseBody.ShouldBe("xyz-handshake");
    }

    [Fact]
    public async Task Events_TamperedSignature_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await TestHarness.CreateAsync();

        var body = JsonSerializer.Serialize(new { type = "event_callback", team_id = "T-acme", @event = new { type = "message", channel_type = "im", user = "U-installer" } });
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sig = "v0=" + new string('a', 64);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenant/connectors/slack/events");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add(SlackEventEndpoints.TimestampHeader, ts);
        request.Headers.Add(SlackEventEndpoints.SignatureHeader, sig);

        var response = await harness.HttpClient.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Events_StaleTimestamp_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await TestHarness.CreateAsync();

        var body = JsonSerializer.Serialize(new { type = "url_verification", team_id = "T-acme", challenge = "x" });
        var ts = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        var sig = Sign(ts, body, SigningSecret);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenant/connectors/slack/events");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add(SlackEventEndpoints.TimestampHeader, ts);
        request.Headers.Add(SlackEventEndpoints.SignatureHeader, sig);

        var response = await harness.HttpClient.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string Sign(string ts, string body, string secret)
    {
        var baseString = $"v0:{ts}:{body}";
        var key = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(baseString));
        return "v0=" + Convert.ToHexStringLower(hash);
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        public HttpClient HttpClient { get; }
        public IHost Host { get; }

        private TestHarness(IHost host, HttpClient httpClient)
        {
            Host = host;
            HttpClient = httpClient;
        }

        public static async Task<TestHarness> CreateAsync()
        {
            var hostBuilder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddAuthorization();
                    services.AddRouting();

                    var tenantContext = Substitute.For<ITenantContext>();
                    tenantContext.CurrentTenantId.Returns(TestTenantId);
                    services.AddSingleton(tenantContext);

                    var dbName = $"EventEndpointTests_{Guid.NewGuid():N}";
                    services.AddDbContext<SpringDbContext>(o =>
                        o.UseInMemoryDatabase(dbName));
                    services.AddScoped<Cvoya.Spring.Dapr.Data.IUnitConnectorBindingRepository, UnitConnectorBindingRepository>();
                    services.AddScoped<Cvoya.Spring.Dapr.Data.ITenantConnectorBindingRepository, TenantConnectorBindingRepository>();
                    services.AddSingleton<ITenantConnectorBindingStore, TenantConnectorBindingStore>();
                    services.AddSingleton<ISlackThreadMapStore, EfSlackThreadMapStore>();
                    services.TryAddEnumerable(ServiceDescriptor.Singleton<ITenantBoundUserExtractor, SlackBoundUserExtractor>());

                    var secretResolver = Substitute.For<ISecretResolver>();
                    secretResolver.ResolveWithPathAsync(Arg.Any<SecretRef>(), Arg.Any<CancellationToken>())
                        .Returns(new SecretResolution(
                            Value: SigningSecret,
                            Path: SecretResolvePath.Direct,
                            EffectiveRef: new SecretRef(SecretScope.Tenant, TestTenantId, "slack/T-acme/signing-secret"),
                            Version: 1));
                    services.AddSingleton(secretResolver);

                    services.AddSingleton(Substitute.For<IThreadRegistry>());
                    services.AddSingleton(Substitute.For<ITenantUserHumanResolver>());
                    services.AddSingleton(Substitute.For<IParticipantDisplayNameResolver>());

                    services.AddSingleton<ISlackWebApiClient>(Substitute.For<ISlackWebApiClient>());
                    services.AddSingleton<ISlackSignatureValidator, SlackSignatureValidator>();
                    services.AddSingleton<IUnboundUserRefusalGate, InMemoryUnboundUserRefusalGate>();
                    services.AddSingleton<ISlackInboundAuditLog, LoggerSlackInboundAuditLog>();
                    services.AddSingleton<ISlackEventDispatcher, SlackEventDispatcher>();
                    services.AddSingleton(Substitute.For<IMessageRouter>());
                });

                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        var group = endpoints.MapGroup("/api/v1/tenant/connectors/slack");
                        group.MapSlackEventEndpoints();
                    });
                });
            });

            var host = await hostBuilder.StartAsync();

            // Seed the binding row so the endpoint can look up the
            // signing secret.
            using (var scope = host.Services.CreateScope())
            {
                var bindingStore = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingStore>();
                var config = new TenantSlackConfig(
                    TeamId: "T-acme",
                    TeamName: "Acme",
                    BotUserId: "U-bot",
                    BotTokenSecretName: "slack/T-acme/bot-token",
                    SigningSecretSecretName: "slack/T-acme/signing-secret",
                    InstallerUserId: "U-installer",
                    SingleUserMode: true,
                    Mode: SlackBindingMode.Workspace,
                    BoundUsers: new[]
                    {
                        new TenantSlackBoundUser("U-installer", new Guid("11111111-1111-1111-1111-111111111111")),
                    });
                var configJson = JsonSerializer.SerializeToElement(config);
                await bindingStore.SetAsync(SlackInstallStore.ConnectorSlug, SlackConnectorType.SlackTypeId, configJson, "T-acme", CancellationToken.None);
            }

            var httpClient = host.GetTestClient();
            return new TestHarness(host, httpClient);
        }

        public async ValueTask DisposeAsync()
        {
            HttpClient.Dispose();
            await Host.StopAsync();
            Host.Dispose();
        }
    }
}
