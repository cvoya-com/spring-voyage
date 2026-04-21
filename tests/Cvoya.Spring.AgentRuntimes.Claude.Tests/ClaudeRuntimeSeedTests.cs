// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Claude.Tests;

using Cvoya.Spring.AgentRuntimes.Claude.Internal;

using Shouldly;

using Xunit;

/// <summary>
/// Pin tests for the embedded seed file. Drift between the file path,
/// the csproj <c>EmbeddedResource</c> entry, and the loader constant
/// breaks the runtime silently — these tests fail loudly on day one.
/// </summary>
public class ClaudeRuntimeSeedTests
{
    [Fact]
    public void Load_Default_ReturnsPopulatedSeed()
    {
        var seed = ClaudeRuntimeSeedLoader.Load();

        seed.Models.Count.ShouldBeGreaterThan(0);
        seed.Models.ShouldContain("claude-opus-4-7");
        seed.Models.ShouldContain("claude-sonnet-4-6");
        seed.Models.ShouldContain("claude-haiku-4-5");
        seed.DefaultModel.ShouldBe("claude-opus-4-7");
        seed.BaseUrl.ShouldBe("https://api.anthropic.com");
        // The seed advertises the runtime's CLI dependency for downstream
        // tooling that surfaces install requirements without parsing the
        // README.
        seed.Extras.ShouldNotBeNull();
        seed.Extras!.Value.GetProperty("requiresCli").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Load_DefaultModelAppearsInModelsList()
    {
        var seed = ClaudeRuntimeSeedLoader.Load();

        seed.Models.ShouldContain(seed.DefaultModel);
    }
}