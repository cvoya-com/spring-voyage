// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Collections.Generic;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="AiProviderRegistry"/> (#1696). Verifies the
/// id-based dispatch the unit-orchestration layer relies on, plus the
/// fail-fast guard against duplicate or blank ids.
/// </summary>
public class AiProviderRegistryTests
{
    [Fact]
    public void Get_ResolvesByExactId()
    {
        var anthropic = ProviderWithId("anthropic");
        var ollama = ProviderWithId("ollama");

        var registry = new AiProviderRegistry([anthropic, ollama]);

        registry.Get("anthropic").ShouldBeSameAs(anthropic);
        registry.Get("ollama").ShouldBeSameAs(ollama);
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        // Manifest values are operator-typed; case-insensitive resolution
        // matches `Anthropic` / `ANTHROPIC` to the canonical lower-case id
        // every implementation reports.
        var anthropic = ProviderWithId("anthropic");

        var registry = new AiProviderRegistry([anthropic]);

        registry.Get("Anthropic").ShouldBeSameAs(anthropic);
        registry.Get("ANTHROPIC").ShouldBeSameAs(anthropic);
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var registry = new AiProviderRegistry([ProviderWithId("anthropic")]);

        registry.Get("openai").ShouldBeNull();
    }

    [Fact]
    public void Get_NullOrWhitespace_ReturnsNull()
    {
        var registry = new AiProviderRegistry([ProviderWithId("anthropic")]);

        registry.Get(null!).ShouldBeNull();
        registry.Get("").ShouldBeNull();
        registry.Get("   ").ShouldBeNull();
    }

    [Fact]
    public void All_ReturnsRegisteredProvidersInOrder()
    {
        var anthropic = ProviderWithId("anthropic");
        var ollama = ProviderWithId("ollama");

        var registry = new AiProviderRegistry([ollama, anthropic]);

        registry.All.ShouldBe(new[] { ollama, anthropic });
    }

    [Fact]
    public void Constructor_DuplicateIds_Throws()
    {
        // Two implementations claiming the same id is a deployment-time
        // misconfiguration. The constructor surfaces it loudly so an
        // operator sees the failure at host start, not on first dispatch.
        var providerA = ProviderWithId("anthropic");
        var providerB = ProviderWithId("anthropic");

        var ex = Should.Throw<InvalidOperationException>(() =>
            new AiProviderRegistry([providerA, providerB]));
        ex.Message.ShouldContain("Duplicate IAiProvider registration for id 'anthropic'");
    }

    [Fact]
    public void Constructor_BlankId_Throws()
    {
        var blank = ProviderWithId("   ");

        var ex = Should.Throw<InvalidOperationException>(() =>
            new AiProviderRegistry([blank]));
        ex.Message.ShouldContain("null/blank Id");
    }

    [Fact]
    public void Constructor_NullProvider_IsSkipped()
    {
        // DI enumerables can in principle yield nulls (factory misbehaviour).
        // The registry treats a null entry as "not registered" rather than
        // crashing every consumer that touches it.
        var anthropic = ProviderWithId("anthropic");

        var registry = new AiProviderRegistry(new IAiProvider[] { null!, anthropic });

        registry.All.ShouldBe(new[] { anthropic });
        registry.Get("anthropic").ShouldBeSameAs(anthropic);
    }

    private static IAiProvider ProviderWithId(string id)
    {
        var provider = Substitute.For<IAiProvider>();
        provider.Id.Returns(id);
        return provider;
    }
}
