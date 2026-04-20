// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Google.Tests;

using Cvoya.Spring.AgentRuntimes.Google;
using Cvoya.Spring.AgentRuntimes.Google.DependencyInjection;
using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCvoyaSpringAgentRuntimeGoogle_RegistersGoogleAgentRuntime()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddCvoyaSpringAgentRuntimeGoogle();

        using var provider = services.BuildServiceProvider();
        var runtimes = provider.GetServices<IAgentRuntime>().ToList();

        runtimes.ShouldContain(r => r is GoogleAgentRuntime);
        runtimes.OfType<GoogleAgentRuntime>().Single().Id.ShouldBe("google");
    }

    [Fact]
    public void AddCvoyaSpringAgentRuntimeGoogle_IsIdempotent_SecondCallDoesNotDoubleRegister()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddCvoyaSpringAgentRuntimeGoogle();
        services.AddCvoyaSpringAgentRuntimeGoogle();

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IAgentRuntime>()
            .OfType<GoogleAgentRuntime>()
            .Count()
            .ShouldBe(1);
    }

    [Fact]
    public void AddCvoyaSpringAgentRuntimeGoogle_RegistersNamedHttpClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddCvoyaSpringAgentRuntimeGoogle();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(GoogleAgentRuntime.HttpClientName);

        client.ShouldNotBeNull();
    }
}