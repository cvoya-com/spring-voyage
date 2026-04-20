// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.AgentRuntimes.Google;
using Cvoya.Spring.AgentRuntimes.Google.DependencyInjection;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Dapr.AgentRuntimes;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Smoke test verifying the Google agent-runtime plug wires through the
/// production <see cref="IAgentRuntimeRegistry"/> resolution path. The
/// registry implementation lives in <c>Cvoya.Spring.Dapr.AgentRuntimes</c>
/// so the host can resolve runtimes by id without taking a direct dependency
/// on any concrete runtime project — this test exercises that seam end-to-end
/// for the Google runtime registered by issue #681.
/// </summary>
public class GoogleAgentRuntimeRegistryIntegrationTests
{
    [Fact]
    public void Registry_ResolvesGoogleRuntime_AfterDiWireUp()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Wire the runtime via its public DI extension and the registry the
        // host normally pulls in via AddCvoyaSpringDapr. Registering the
        // registry directly keeps the test focused on the runtime-resolution
        // seam without dragging in the full Dapr + EF Core registration graph.
        services.AddCvoyaSpringAgentRuntimeGoogle();
        services.AddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentRuntimeRegistry>();

        var runtime = registry.Get("google");

        runtime.ShouldNotBeNull();
        runtime!.ShouldBeOfType<GoogleAgentRuntime>();
        runtime.Id.ShouldBe("google");
        runtime.DisplayName.ShouldBe("Google AI (dapr-agent + Google AI API)");
        runtime.ToolKind.ShouldBe("dapr-agent");

        // Lookup is case-insensitive per the contract on IAgentRuntimeRegistry.
        registry.Get("GOOGLE").ShouldBeSameAs(runtime);
        registry.Get("Google").ShouldBeSameAs(runtime);
    }

    [Fact]
    public void Registry_DefaultModels_ExposesSeededGoogleCatalogue()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCvoyaSpringAgentRuntimeGoogle();
        services.AddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentRuntimeRegistry>();

        var runtime = registry.Get("google");
        runtime.ShouldNotBeNull();

        runtime!.DefaultModels
            .Select(m => m.Id)
            .ShouldBe(new[] { "gemini-2.5-pro", "gemini-2.5-flash" });
    }
}