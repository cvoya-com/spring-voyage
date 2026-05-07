// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.AgentRuntimes;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Dapr.AgentRuntimes;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies that the catalogue-backed <see cref="AgentRuntimeRegistry"/>
/// projects every catalogue runtime entry through
/// <see cref="CatalogAgentRuntimeAdapter"/> with the correct case-
/// insensitive lookup semantics.
/// </summary>
public class AgentRuntimeRegistryTests
{
    [Fact]
    public void All_EmptyCatalog_IsEmpty()
    {
        var registry = new AgentRuntimeRegistry(BuildCatalog(Array.Empty<Core.Catalog.AgentRuntime>(), Array.Empty<ModelProvider>()));

        registry.All.ShouldBeEmpty();
    }

    [Fact]
    public void All_EnumeratesEveryCatalogRuntime()
    {
        var providers = new[]
        {
            new ModelProvider("alpha-prov", "Alpha Provider", "https://alpha.example", "/v1/models", "openai-compatible", new[] { AuthMethod.ApiKey }, new LlmApiContract("openai", "v1"), Array.Empty<string>()),
            new ModelProvider("beta-prov", "Beta Provider", "https://beta.example", "/v1/models", "openai-compatible", new[] { AuthMethod.ApiKey }, new LlmApiContract("openai", "v1"), Array.Empty<string>()),
        };
        var runtimes = new[]
        {
            new Core.Catalog.AgentRuntime("alpha", "Alpha", "alpha:latest", "spring-voyage-agent",
                new ThreadBinding(ThreadBindingKind.EnvVar, EnvVarName: "T_ID"),
                new SystemPromptInjection(SystemPromptInjectionKind.EnvVar, EnvVarName: "P_ID"),
                new[] { new AgentRuntimeProviderEdge("alpha-prov", AuthMethod.ApiKey, "ALPHA_KEY") }),
            new Core.Catalog.AgentRuntime("beta", "Beta", "beta:latest", "spring-voyage-agent",
                new ThreadBinding(ThreadBindingKind.EnvVar, EnvVarName: "T_ID"),
                new SystemPromptInjection(SystemPromptInjectionKind.EnvVar, EnvVarName: "P_ID"),
                new[] { new AgentRuntimeProviderEdge("beta-prov", AuthMethod.ApiKey, "BETA_KEY") }),
        };

        var registry = new AgentRuntimeRegistry(BuildCatalog(runtimes, providers));

        registry.All.Count.ShouldBe(2);
        registry.All.ShouldContain(r => r.Id == "alpha");
        registry.All.ShouldContain(r => r.Id == "beta");
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var providers = new[]
        {
            new ModelProvider("alpha-prov", "Alpha", "https://alpha.example", "/v1/models", "openai-compatible", new[] { AuthMethod.ApiKey }, new LlmApiContract("openai", "v1"), Array.Empty<string>()),
        };
        var runtimes = new[]
        {
            new Core.Catalog.AgentRuntime("alpha", "Alpha", "alpha:latest", "spring-voyage-agent",
                new ThreadBinding(ThreadBindingKind.EnvVar, EnvVarName: "T_ID"),
                new SystemPromptInjection(SystemPromptInjectionKind.EnvVar, EnvVarName: "P_ID"),
                new[] { new AgentRuntimeProviderEdge("alpha-prov", AuthMethod.ApiKey, "ALPHA_KEY") }),
        };
        var registry = new AgentRuntimeRegistry(BuildCatalog(runtimes, providers));

        registry.Get("gamma").ShouldBeNull();
    }

    [Fact]
    public void Get_CaseInsensitive_Match()
    {
        var providers = new[]
        {
            new ModelProvider("alpha-prov", "Alpha", "https://alpha.example", "/v1/models", "openai-compatible", new[] { AuthMethod.ApiKey }, new LlmApiContract("openai", "v1"), Array.Empty<string>()),
        };
        var runtimes = new[]
        {
            new Core.Catalog.AgentRuntime("Alpha", "Alpha Display", "alpha:latest", "spring-voyage-agent",
                new ThreadBinding(ThreadBindingKind.EnvVar, EnvVarName: "T_ID"),
                new SystemPromptInjection(SystemPromptInjectionKind.EnvVar, EnvVarName: "P_ID"),
                new[] { new AgentRuntimeProviderEdge("alpha-prov", AuthMethod.ApiKey, "ALPHA_KEY") }),
        };
        var registry = new AgentRuntimeRegistry(BuildCatalog(runtimes, providers));

        registry.Get("alpha").ShouldNotBeNull();
        registry.Get("ALPHA").ShouldNotBeNull();
        registry.Get("AlPhA").ShouldNotBeNull();
    }

    [Fact]
    public void Get_NullOrWhitespace_ReturnsNull()
    {
        var registry = new AgentRuntimeRegistry(BuildCatalog(Array.Empty<Core.Catalog.AgentRuntime>(), Array.Empty<ModelProvider>()));

        registry.Get(null!).ShouldBeNull();
        registry.Get(string.Empty).ShouldBeNull();
        registry.Get("   ").ShouldBeNull();
    }

    [Fact]
    public void Adapter_DerivesKindFromLauncherAndSecretNameFromEdge()
    {
        var providers = new[]
        {
            new ModelProvider("anthropic", "Anthropic", "https://api.anthropic.com", "/v1/models", "anthropic", new[] { AuthMethod.Oauth }, new LlmApiContract("anthropic", "v1"), new[] { "claude-opus-4-7" }),
        };
        var runtimes = new[]
        {
            new Core.Catalog.AgentRuntime("claude-code", "Claude Code", "claude-code:latest", "claude-code-cli",
                new ThreadBinding(ThreadBindingKind.CliArg, ArgName: "--resume"),
                new SystemPromptInjection(SystemPromptInjectionKind.File, FilePath: "AGENTS.md"),
                new[] { new AgentRuntimeProviderEdge("anthropic", AuthMethod.Oauth, "CLAUDE_CODE_OAUTH_TOKEN") }),
        };
        var registry = new AgentRuntimeRegistry(BuildCatalog(runtimes, providers));

        var runtime = registry.Get("claude-code")!;
        runtime.Kind.ShouldBe("claude-code-cli");
        runtime.CredentialEnvVar.ShouldBe("CLAUDE_CODE_OAUTH_TOKEN");
        runtime.CredentialSchema.Kind.ShouldBe(AgentRuntimeCredentialKind.OAuthToken);
        runtime.CredentialSecretName.ShouldBe("anthropic-oauth");
        runtime.DefaultImage.ShouldBe("claude-code:latest");
        runtime.DefaultModels.ShouldContain(m => m.Id == "claude-opus-4-7");
    }

    private static IRuntimeCatalog BuildCatalog(
        IReadOnlyList<Core.Catalog.AgentRuntime> runtimes,
        IReadOnlyList<ModelProvider> providers)
        => new TestRuntimeCatalog(runtimes, providers);

    private sealed class TestRuntimeCatalog(
        IReadOnlyList<Core.Catalog.AgentRuntime> runtimes,
        IReadOnlyList<ModelProvider> providers) : IRuntimeCatalog
    {
        public IReadOnlyList<Core.Catalog.AgentRuntime> AgentRuntimes => runtimes;

        public IReadOnlyList<ModelProvider> ModelProviders => providers;

        public Core.Catalog.AgentRuntime? GetAgentRuntime(string id) =>
            runtimes.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

        public ModelProvider? GetModelProvider(string id) =>
            providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
