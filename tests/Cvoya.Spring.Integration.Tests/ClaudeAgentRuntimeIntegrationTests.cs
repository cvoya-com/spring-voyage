// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using Cvoya.Spring.AgentRuntimes.Claude;
using Cvoya.Spring.AgentRuntimes.Claude.DependencyInjection;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.AgentRuntimes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests covering the Claude agent runtime as it is wired in
/// the host (#679, refreshed for T-03 / #945). Verifies the runtime
/// resolves through <see cref="IAgentRuntimeRegistry"/> after the host
/// calls <c>AddCvoyaSpringAgentRuntimeClaude()</c>, that the embedded seed
/// catalog is the source of <see cref="IAgentRuntime.DefaultModels"/>, and
/// that <see cref="IAgentRuntime.GetProbeSteps"/> returns a well-formed
/// probe plan for the backend <c>UnitValidationWorkflow</c>.
/// </summary>
public class ClaudeAgentRuntimeIntegrationTests
{
    [Fact]
    public void Registry_ResolvesClaudeAfterAddCvoyaSpringAgentRuntimeClaude()
    {
        using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IAgentRuntimeRegistry>();

        var runtime = registry.Get("claude");
        runtime.ShouldNotBeNull();
        runtime!.Id.ShouldBe("claude");
        runtime.ToolKind.ShouldBe("claude-code-cli");
        runtime.DisplayName.ShouldBe("Claude (Claude Code CLI + Anthropic API)");

        // Case-insensitive lookup is part of the registry contract.
        registry.Get("CLAUDE").ShouldNotBeNull();
        registry.All.ShouldContain(r => r.Id == "claude");
    }

    [Fact]
    public void DefaultModels_ComeFromSeedFile()
    {
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<IAgentRuntimeRegistry>().Get("claude")!;

        var ids = runtime.DefaultModels.Select(m => m.Id).ToArray();
        ids.ShouldBe(new[]
        {
            "claude-opus-4-7",
            "claude-sonnet-4-6",
            "claude-haiku-4-5",
        });
    }

    [Fact]
    public void GetProbeSteps_ProducesExpectedBackendValidationPlan()
    {
        // T-03 (#945) replaces the host-side VerifyContainerBaselineAsync
        // probe with a declarative in-container probe plan consumed by the
        // UnitValidationWorkflow. Regression gate: the runtime must surface
        // a tool + credential + model trio the workflow can execute.
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<IAgentRuntimeRegistry>().Get("claude")!;

        var config = new AgentRuntimeInstallConfig(
            Models: new[] { "claude-sonnet-4-6" },
            DefaultModel: "claude-sonnet-4-6",
            BaseUrl: null);

        var steps = runtime.GetProbeSteps(config, credential: "sk-ant-api03-example");
        steps.ShouldNotBeNull();
        steps.Select(s => s.Step).ShouldBe(new[]
        {
            UnitValidationStep.VerifyingTool,
            UnitValidationStep.ValidatingCredential,
            UnitValidationStep.ResolvingModel,
        });
        steps.ShouldAllBe(s =>
            s.InterpretOutput != null
            && s.Args.Count > 0
            && s.Timeout > TimeSpan.Zero
            && s.Timeout < TimeSpan.FromMinutes(5));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Mimic what AddCvoyaSpringDapr does for the registry. Calling
        // the full AddCvoyaSpringDapr would pull in EF Core / Dapr /
        // every other piece; the registry contract test only needs the
        // registry itself and the runtime registrations.
        services.TryAddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();
        services.AddCvoyaSpringAgentRuntimeClaude();

        return services.BuildServiceProvider();
    }
}