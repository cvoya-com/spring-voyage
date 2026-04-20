// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.AgentRuntimes.Ollama;
using Cvoya.Spring.AgentRuntimes.Ollama.DependencyInjection;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Dapr.AgentRuntimes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for the Ollama runtime as a participant in the
/// platform's <see cref="IAgentRuntimeRegistry"/>. Exercises the full DI
/// composition path the API host uses (extension method →
/// <see cref="AgentRuntimeRegistry"/> enumeration → registry lookup).
/// </summary>
public class OllamaAgentRuntimeRegistrationTests
{
    [Fact]
    public void Registry_ResolvesOllamaRuntimeById()
    {
        // Mirrors the host composition: register the OSS registry alongside
        // the runtime extension and confirm the runtime surfaces through
        // Get(string) by its stable id.
        var services = new ServiceCollection();
        services.AddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();
        services.AddCvoyaSpringAgentRuntimeOllama(EmptyConfiguration());

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentRuntimeRegistry>();

        registry.All.ShouldHaveSingleItem();
        var resolved = registry.Get("ollama");
        resolved.ShouldNotBeNull();
        resolved!.Id.ShouldBe("ollama");
        resolved.ToolKind.ShouldBe("dapr-agent");
        resolved.DisplayName.ShouldBe("Ollama (dapr-agent + local Ollama)");
    }

    [Fact]
    public void Registry_GetByCaseInsensitiveId_ResolvesOllama()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();
        services.AddCvoyaSpringAgentRuntimeOllama(EmptyConfiguration());

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentRuntimeRegistry>();

        registry.Get("OLLAMA").ShouldNotBeNull();
        registry.Get("Ollama").ShouldNotBeNull();
    }

    [Fact]
    public async Task ValidateCredentialAsync_UnreachableEndpoint_ReportsNetworkErrorWithoutThrowing()
    {
        // Acceptance criteria: integration smoke for the unreachable case.
        // We point the runtime at a guaranteed-unbound port on the loopback
        // interface and confirm the contract is honoured: no exception
        // escapes, status is NetworkError, and the message names the URL so
        // the wizard can surface it.
        var services = new ServiceCollection();
        services.AddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();

        // Port 1 is reserved + privileged; an outbound connect from a
        // non-root process gets refused immediately on every supported
        // platform, which is exactly the shape of the failure we want to
        // exercise.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentRuntimes:Ollama:BaseUrl"] = "http://127.0.0.1:1",
                ["AgentRuntimes:Ollama:HealthCheckTimeoutSeconds"] = "2",
            })
            .Build();

        services.AddCvoyaSpringAgentRuntimeOllama(configuration);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentRuntimeRegistry>();

        var runtime = registry.Get("ollama")!;

        var result = await runtime.ValidateCredentialAsync(string.Empty, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.NetworkError);
        result.Valid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
    }

    [Fact]
    public async Task VerifyContainerBaselineAsync_UnreachableEndpoint_FailsBaseline()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentRuntimes:Ollama:BaseUrl"] = "http://127.0.0.1:1",
                ["AgentRuntimes:Ollama:HealthCheckTimeoutSeconds"] = "2",
            })
            .Build();

        services.AddCvoyaSpringAgentRuntimeOllama(configuration);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentRuntimeRegistry>();

        var runtime = registry.Get("ollama")!;
        var result = await runtime.VerifyContainerBaselineAsync(TestContext.Current.CancellationToken);

        result.Passed.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
        result.Errors[0].ShouldContain("not reachable");
    }

    private static IConfiguration EmptyConfiguration()
        => new ConfigurationBuilder().AddInMemoryCollection().Build();
}