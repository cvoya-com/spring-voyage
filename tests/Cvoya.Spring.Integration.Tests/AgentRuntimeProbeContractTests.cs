// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.AgentRuntimes.Claude.DependencyInjection;
using Cvoya.Spring.AgentRuntimes.Google.DependencyInjection;
using Cvoya.Spring.AgentRuntimes.Ollama.DependencyInjection;
using Cvoya.Spring.AgentRuntimes.OpenAI.DependencyInjection;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.AgentRuntimes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Shouldly;

using Xunit;

/// <summary>
/// Cross-runtime contract smoke test for T-03 (#945). Catches wiring-level
/// mistakes — every OSS runtime must produce a non-null probe plan with
/// non-null <c>InterpretOutput</c> delegates, bounded timeouts, and no
/// <see cref="UnitValidationStep.PullingImage"/> step (the image pull is
/// dispatcher-owned).
/// </summary>
public class AgentRuntimeProbeContractTests
{
    [Fact]
    public void AllOssRuntimes_EmitBoundedProbePlans()
    {
        using var provider = BuildAllRuntimes();
        var registry = provider.GetRequiredService<IAgentRuntimeRegistry>();

        registry.All.Count.ShouldBeGreaterThanOrEqualTo(4);

        foreach (var runtime in registry.All)
        {
            var config = new AgentRuntimeInstallConfig(
                Models: runtime.DefaultModels.Count > 0
                    ? new[] { runtime.DefaultModels[0].Id }
                    : new[] { "test-model" },
                DefaultModel: runtime.DefaultModels.Count > 0
                    ? runtime.DefaultModels[0].Id
                    : "test-model",
                BaseUrl: null);

            var steps = runtime.GetProbeSteps(config, credential: "placeholder-credential");

            steps.ShouldNotBeNull($"runtime {runtime.Id} returned a null probe plan");
            foreach (var step in steps)
            {
                step.ShouldNotBeNull($"runtime {runtime.Id} returned a null step");
                step.InterpretOutput.ShouldNotBeNull($"runtime {runtime.Id} step {step.Step} has no interpreter");
                step.Args.ShouldNotBeNull();
                step.Args.Count.ShouldBeGreaterThan(0, $"runtime {runtime.Id} step {step.Step} has empty args");
                step.Timeout.ShouldBeGreaterThan(TimeSpan.Zero, $"runtime {runtime.Id} step {step.Step} timeout is non-positive");
                step.Timeout.ShouldBeLessThan(TimeSpan.FromMinutes(5),
                    $"runtime {runtime.Id} step {step.Step} timeout exceeds the 5-minute budget");
                step.Step.ShouldNotBe(UnitValidationStep.PullingImage,
                    $"runtime {runtime.Id} improperly returned a PullingImage step — the dispatcher owns that step");
            }
        }
    }

    [Fact]
    public void EveryKnownRuntime_IsPresentInRegistry()
    {
        using var provider = BuildAllRuntimes();
        var registry = provider.GetRequiredService<IAgentRuntimeRegistry>();

        registry.Get("claude").ShouldNotBeNull();
        registry.Get("openai").ShouldNotBeNull();
        registry.Get("google").ShouldNotBeNull();
        registry.Get("ollama").ShouldNotBeNull();
    }

    private static ServiceProvider BuildAllRuntimes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.TryAddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentRuntimes:Ollama:BaseUrl"] = "http://localhost:11434",
            })
            .Build();

        services.AddCvoyaSpringAgentRuntimeClaude();
        services.AddCvoyaSpringAgentRuntimeOpenAI();
        services.AddCvoyaSpringAgentRuntimeGoogle();
        services.AddCvoyaSpringAgentRuntimeOllama(configuration);

        return services.BuildServiceProvider();
    }
}