// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Ollama.Tests;

using Cvoya.Spring.AgentRuntimes.Ollama;

using Shouldly;

using Xunit;

public class OllamaSeedTests
{
    [Fact]
    public void Load_ReturnsParsedSeedFile()
    {
        var seed = OllamaSeed.Load();

        seed.ShouldNotBeNull();
        seed.Models.ShouldNotBeNull();
        seed.Models!.Count.ShouldBeGreaterThan(0);
        seed.DefaultModel.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Load_DefaultModelIsContainedInModelsList()
    {
        // The contract documented in
        // src/Cvoya.Spring.Core/AgentRuntimes/README.md#seed-file-schema
        // requires defaultModel to be present in models — codify it.
        var seed = OllamaSeed.Load();

        seed.Models!.ShouldContain(seed.DefaultModel!);
    }

    [Fact]
    public void Load_IncludesCuratedFamilyMembers()
    {
        // Acceptance criteria: DefaultModels comes from the curated list
        // today (the StaticFallback["ollama"] entries before the seam).
        // Asserting on a representative subset (rather than the full list)
        // keeps the test from being brittle to additive changes.
        var seed = OllamaSeed.Load();

        seed.Models!.ShouldContain("llama3.2:3b");
        seed.Models!.ShouldContain("qwen2.5:14b");
    }

    [Fact]
    public void Load_PopulatesDefaultBaseUrl()
    {
        var seed = OllamaSeed.Load();

        seed.BaseUrl.ShouldNotBeNullOrWhiteSpace();
        seed.BaseUrl!.ShouldStartWith("http");
    }

    [Fact]
    public void ToDescriptors_ProjectsEverySeedModel()
    {
        var seed = OllamaSeed.Load();

        var descriptors = OllamaSeed.ToDescriptors(seed);

        descriptors.Count.ShouldBe(seed.Models!.Count);
        descriptors.Select(d => d.Id).ShouldBe(seed.Models!);
        descriptors.ShouldAllBe(d => d.ContextWindow == null);
    }
}