// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.AgentRuntimes;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Dapr.AgentRuntimes;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

public class AgentRuntimeRegistryTests
{
    [Fact]
    public void All_NoRuntimesRegistered_IsEmpty()
    {
        var registry = new AgentRuntimeRegistry(Array.Empty<IAgentRuntime>());

        registry.All.ShouldBeEmpty();
    }

    [Fact]
    public void All_EnumeratesEveryRegisteredRuntime()
    {
        var r1 = new FakeAgentRuntime("alpha", "Alpha");
        var r2 = new FakeAgentRuntime("beta", "Beta");

        var registry = new AgentRuntimeRegistry(new IAgentRuntime[] { r1, r2 });

        registry.All.Count.ShouldBe(2);
        registry.All.ShouldContain(r1);
        registry.All.ShouldContain(r2);
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var registry = new AgentRuntimeRegistry(new IAgentRuntime[]
        {
            new FakeAgentRuntime("alpha", "Alpha"),
        });

        registry.Get("gamma").ShouldBeNull();
    }

    [Fact]
    public void Get_ExactMatch_ReturnsRuntime()
    {
        var alpha = new FakeAgentRuntime("alpha", "Alpha");
        var registry = new AgentRuntimeRegistry(new IAgentRuntime[] { alpha });

        registry.Get("alpha").ShouldBe(alpha);
    }

    [Fact]
    public void Get_CaseInsensitiveMatch_ReturnsRuntime()
    {
        var alpha = new FakeAgentRuntime("Alpha", "Alpha Display");
        var registry = new AgentRuntimeRegistry(new IAgentRuntime[] { alpha });

        registry.Get("alpha").ShouldBe(alpha);
        registry.Get("ALPHA").ShouldBe(alpha);
        registry.Get("AlPhA").ShouldBe(alpha);
    }

    [Fact]
    public void Get_NullOrWhitespace_ReturnsNull()
    {
        var registry = new AgentRuntimeRegistry(new IAgentRuntime[]
        {
            new FakeAgentRuntime("alpha", "Alpha"),
        });

        registry.Get(null!).ShouldBeNull();
        registry.Get(string.Empty).ShouldBeNull();
        registry.Get("   ").ShouldBeNull();
    }

    [Fact]
    public void DefaultDiRegistration_ResolvesEveryRegisteredIAgentRuntime()
    {
        // Confirms that when a host does
        //   services.AddSingleton<IAgentRuntime>(...) x N
        //   services.TryAddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>()
        // the registry transparently picks every registered runtime up.
        var services = new ServiceCollection();
        services.AddSingleton<IAgentRuntime>(new FakeAgentRuntime("alpha", "Alpha"));
        services.AddSingleton<IAgentRuntime>(new FakeAgentRuntime("beta", "Beta"));
        services.AddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentRuntimeRegistry>();

        registry.All.Count.ShouldBe(2);
        registry.Get("alpha").ShouldNotBeNull();
        registry.Get("BETA").ShouldNotBeNull();
    }

    private sealed class FakeAgentRuntime(string id, string displayName) : IAgentRuntime
    {
        public string Id { get; } = id;

        public string DisplayName { get; } = displayName;

        public string ToolKind => "fake";

        public AgentRuntimeCredentialSchema CredentialSchema { get; } =
            new(AgentRuntimeCredentialKind.None);

        public string CredentialSecretName => string.Empty;

        public IReadOnlyList<ModelDescriptor> DefaultModels { get; } = Array.Empty<ModelDescriptor>();

        public IReadOnlyList<ProbeStep> GetProbeSteps(AgentRuntimeInstallConfig config, string credential) =>
            Array.Empty<ProbeStep>();

        public Task<FetchLiveModelsResult> FetchLiveModelsAsync(
            string credential,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(FetchLiveModelsResult.Unsupported("Fake runtime does not expose a live catalog."));

        public bool IsCredentialFormatAccepted(string credential, CredentialDispatchPath dispatchPath) => true;
    }
}