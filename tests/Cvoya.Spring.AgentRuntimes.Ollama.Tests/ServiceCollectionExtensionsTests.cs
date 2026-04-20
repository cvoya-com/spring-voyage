// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Ollama.Tests;

using Cvoya.Spring.AgentRuntimes.Ollama;
using Cvoya.Spring.AgentRuntimes.Ollama.DependencyInjection;
using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCvoyaSpringAgentRuntimeOllama_RegistersRuntimeAsIAgentRuntime()
    {
        var services = BuildServices();

        using var provider = services.BuildServiceProvider();

        var runtimes = provider.GetServices<IAgentRuntime>().ToArray();

        runtimes.ShouldHaveSingleItem();
        runtimes[0].ShouldBeOfType<OllamaAgentRuntime>();
        runtimes[0].Id.ShouldBe("ollama");
    }

    [Fact]
    public void AddCvoyaSpringAgentRuntimeOllama_BindsConfigurationSection()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentRuntimes:Ollama:BaseUrl"] = "http://configured:9999",
                ["AgentRuntimes:Ollama:HealthCheckTimeoutSeconds"] = "12",
            })
            .Build();

        services.AddCvoyaSpringAgentRuntimeOllama(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OllamaAgentRuntimeOptions>>().Value;

        options.BaseUrl.ShouldBe("http://configured:9999");
        options.HealthCheckTimeoutSeconds.ShouldBe(12);
    }

    [Fact]
    public void AddCvoyaSpringAgentRuntimeOllama_RegistersNamedHttpClient()
    {
        var services = BuildServices();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // The named client must resolve — otherwise the runtime's probe would
        // throw at request time. CreateClient never returns null.
        factory.CreateClient(OllamaAgentRuntime.HttpClientName).ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringAgentRuntimeOllama_CalledTwice_DoesNotDoubleRegisterRuntime()
    {
        // Composite hosts that wire DI through multiple entry points should
        // not surface two "ollama" entries from the registry.
        var services = BuildServices();
        services.AddCvoyaSpringAgentRuntimeOllama(EmptyConfiguration());

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IAgentRuntime>().Count().ShouldBe(1);
    }

    [Fact]
    public void AddCvoyaSpringAgentRuntimeOllama_PreservesPreRegisteredOverride()
    {
        // The cloud host can pre-register a tenant-scoped variant of the
        // strongly-typed runtime; the extension must not overwrite it.
        // Register against the base type so the TryAddSingleton in the
        // extension sees the slot is filled.
        var services = new ServiceCollection();
        var custom = new CustomOllamaAgentRuntime();
        services.AddSingleton<OllamaAgentRuntime>(custom);

        services.AddCvoyaSpringAgentRuntimeOllama(EmptyConfiguration());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<OllamaAgentRuntime>().ShouldBeSameAs(custom);
    }

    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddCvoyaSpringAgentRuntimeOllama(EmptyConfiguration());
        return services;
    }

    private static IConfiguration EmptyConfiguration()
        => new ConfigurationBuilder().AddInMemoryCollection().Build();

    private sealed class CustomOllamaAgentRuntime : OllamaAgentRuntime
    {
        public CustomOllamaAgentRuntime()
            : base(
                new StubHttpClientFactory(new StubHttpMessageHandler((_, _) =>
                    Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)))),
                Options.Create(new OllamaAgentRuntimeOptions()))
        {
        }
    }
}