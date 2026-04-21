// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.AgentRuntimes.Ollama;
using Cvoya.Spring.AgentRuntimes.Ollama.DependencyInjection;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.AgentRuntimes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for the Ollama runtime as a participant in the
/// platform's <see cref="IAgentRuntimeRegistry"/>. Exercises the full DI
/// composition path the API host uses (extension method →
/// <see cref="AgentRuntimeRegistry"/> enumeration → registry lookup), and
/// the T-03 probe-contract (#945) surface.
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
    public void GetProbeSteps_SkipsValidatingCredential_ForCredentialLessRuntime()
    {
        // T-03 (#945): Ollama is credential-less, so its probe plan must
        // skip the ValidatingCredential step rather than emit a no-op.
        var services = new ServiceCollection();
        services.AddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();
        services.AddCvoyaSpringAgentRuntimeOllama(EmptyConfiguration());

        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IAgentRuntimeRegistry>().Get("ollama")!;

        var config = new AgentRuntimeInstallConfig(
            Models: new[] { "llama3.2:3b" },
            DefaultModel: "llama3.2:3b",
            BaseUrl: "http://localhost:11434");

        var steps = runtime.GetProbeSteps(config, credential: string.Empty);

        steps.Select(s => s.Step).ShouldBe(new[]
        {
            UnitValidationStep.VerifyingTool,
            UnitValidationStep.ResolvingModel,
        });
        steps.Select(s => s.Step).ShouldNotContain(UnitValidationStep.ValidatingCredential);
    }

    private static IConfiguration EmptyConfiguration()
        => new ConfigurationBuilder().AddInMemoryCollection().Build();
}