// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Net;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors.Client;
using global::Dapr.Client;
using global::Dapr.Workflow;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for ADR-0039 G6: API requests that still carry
/// <c>containerRuntime</c> are rejected with the structured
/// <c>LegacyContainerRuntimeField</c> problem before the legacy field can be
/// silently ignored by DTO binding.
/// </summary>
public class LegacyContainerRuntimeFieldIntegrationTests
    : IClassFixture<LegacyContainerRuntimeFieldIntegrationTests.Factory>
{
    private const string LegacyContainerRuntimeCode = "LegacyContainerRuntimeField";

    private const string LegacyMigrationHint =
        "containerRuntime is removed in ADR-0039; the container runtime is platform configuration.";

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public LegacyContainerRuntimeFieldIntegrationTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        _factory.DirectoryService.ClearReceivedCalls();
        _factory.AgentExecutionStore.ClearReceivedCalls();
    }

    [Fact]
    public async Task CreateAgent_BodyWithContainerRuntime_Returns400LegacyContainerRuntimeField()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        var body = $$"""
            {
              "displayName": "Ada",
              "description": "Test agent",
              "role": null,
              "unitIds": ["{{unitGuid}}"],
              "containerRuntime": "docker"
            }
            """;

        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/api/v1/tenant/agents", content, ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertLegacyContainerRuntimeProblemAsync(response, ct);
        await _factory.DirectoryService.DidNotReceive()
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutAgentExecution_BodyWithContainerRuntime_Returns400LegacyContainerRuntimeField()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == Address.AgentScheme), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address(Address.AgentScheme, agentGuid),
                agentGuid,
                "Test Agent",
                "Test agent",
                null,
                DateTimeOffset.UtcNow));

        using var content = new StringContent(
            """{"image":"ghcr.io/example/agent:latest","containerRuntime":"docker","runtime":"spring-voyage","hosting":"ephemeral"}""",
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PutAsync(
            $"/api/v1/tenant/agents/{agentGuid:N}/execution",
            content,
            ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertLegacyContainerRuntimeProblemAsync(response, ct);
        await _factory.AgentExecutionStore.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<AgentExecutionShape>(), Arg.Any<CancellationToken>());
    }

    private static async Task AssertLegacyContainerRuntimeProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("code").GetString().ShouldBe(LegacyContainerRuntimeCode);
        root.GetProperty("detail").GetString().ShouldBe(LegacyMigrationHint);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public IDirectoryService DirectoryService { get; } = Substitute.For<IDirectoryService>();
        public IAgentExecutionStore AgentExecutionStore { get; } = CreateAgentExecutionStore();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("LocalDev", "true");
            builder.UseSetting("ConnectionStrings:SpringDb",
                "Host=test;Database=test;Username=test;Password=test");

            builder.ConfigureServices(services =>
            {
                services.RemoveDaprWorkflowWorker();
                ReplaceSpringDbContext(services);
                ReplaceDaprBackedServices(services);

                var actorProxyFactory = Substitute.For<IActorProxyFactory>();
                var agentProxyResolver = Substitute.For<IAgentProxyResolver>();
                var permissionService = Substitute.For<IPermissionService>();

                services.AddSingleton(DirectoryService);
                services.AddSingleton(actorProxyFactory);
                services.AddSingleton(agentProxyResolver);
                services.AddSingleton(Substitute.For<IStateStore>());
                services.AddSingleton(Substitute.For<ICostTracker>());
                services.AddSingleton(Substitute.For<DaprClient>());
                services.AddSingleton(AgentExecutionStore);
                services.AddSingleton(new DirectoryCache());
                services.AddDaprWorkflow(options => { });
                services.RemoveDaprWorkflowWorker();

                services.AddSingleton(sp =>
                {
                    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                    return new MessageRouter(
                        DirectoryService,
                        agentProxyResolver,
                        permissionService,
                        loggerFactory,
                        scopeFactory);
                });
            });
        }

        private static IAgentExecutionStore CreateAgentExecutionStore()
        {
            var store = Substitute.For<IAgentExecutionStore>();
            store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<AgentExecutionShape?>(null));
            return store;
        }

        private static void ReplaceSpringDbContext(IServiceCollection services)
        {
            var descriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<SpringDbContext>)
                         || d.ServiceType == typeof(DbContextOptions)
                         || d.ServiceType == typeof(SpringDbContext)
                         || (d.ServiceType.FullName?.StartsWith(
                                "Microsoft.EntityFrameworkCore.", StringComparison.Ordinal) ?? false)
                         || (d.ServiceType.FullName?.StartsWith(
                                "Npgsql.", StringComparison.Ordinal) ?? false))
                .ToList();

            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<SpringDbContext>(options =>
                options.UseInMemoryDatabase($"LegacyContainerRuntime_{Guid.NewGuid():N}"));
        }

        private static void ReplaceDaprBackedServices(IServiceCollection services)
        {
            var typesToRemove = new[]
            {
                typeof(IDirectoryService),
                typeof(MessageRouter),
                typeof(DirectoryCache),
                typeof(IActorProxyFactory),
                typeof(IAgentProxyResolver),
                typeof(IStateStore),
                typeof(ICostTracker),
                typeof(IAgentExecutionStore),
            };

            var descriptors = services
                .Where(d => typesToRemove.Contains(d.ServiceType))
                .ToList();

            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }
        }
    }
}
