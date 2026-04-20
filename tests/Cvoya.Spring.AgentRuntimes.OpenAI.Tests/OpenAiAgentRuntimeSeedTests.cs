// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.OpenAI.Tests;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.AgentRuntimes.OpenAI;

using Shouldly;

using Xunit;

/// <summary>
/// Round-trip and validation tests for <see cref="OpenAiAgentRuntimeSeed"/>
/// and its loader. Seed files are part of the public surface of the
/// runtime — drift here is a versioned breaking change.
/// </summary>
public class OpenAiAgentRuntimeSeedTests
{
    [Fact]
    public void LoadFromStream_AllFieldsRoundTrip()
    {
        const string json = """
        {
          "models": ["gpt-4o", "gpt-4o-mini", "o3-mini"],
          "defaultModel": "gpt-4o",
          "baseUrl": "https://api.openai.com"
        }
        """;

        var seed = OpenAiAgentRuntimeSeedLoader.LoadFromStream(
            new MemoryStream(Encoding.UTF8.GetBytes(json)),
            sourceDescription: "<test>");

        seed.Models.ShouldBe(new[] { "gpt-4o", "gpt-4o-mini", "o3-mini" });
        seed.DefaultModel.ShouldBe("gpt-4o");
        seed.BaseUrl.ShouldBe("https://api.openai.com");
    }

    [Fact]
    public void LoadFromStream_BaseUrl_IsOptional()
    {
        const string json = """
        {
          "models": ["gpt-4o-mini"],
          "defaultModel": "gpt-4o-mini"
        }
        """;

        var seed = OpenAiAgentRuntimeSeedLoader.LoadFromStream(
            new MemoryStream(Encoding.UTF8.GetBytes(json)),
            sourceDescription: "<test>");

        seed.BaseUrl.ShouldBeNull();
    }

    [Fact]
    public void LoadFromStream_EmptyModels_Throws()
    {
        const string json = """
        {
          "models": [],
          "defaultModel": "gpt-4o"
        }
        """;

        var ex = Should.Throw<InvalidDataException>(() =>
            OpenAiAgentRuntimeSeedLoader.LoadFromStream(
                new MemoryStream(Encoding.UTF8.GetBytes(json)),
                sourceDescription: "<test>"));

        ex.Message.ShouldContain("at least one model");
    }

    [Fact]
    public void LoadFromStream_DefaultModelNotInModels_Throws()
    {
        const string json = """
        {
          "models": ["gpt-4o"],
          "defaultModel": "gpt-99-not-listed"
        }
        """;

        var ex = Should.Throw<InvalidDataException>(() =>
            OpenAiAgentRuntimeSeedLoader.LoadFromStream(
                new MemoryStream(Encoding.UTF8.GetBytes(json)),
                sourceDescription: "<test>"));

        ex.Message.ShouldContain("defaultModel");
        ex.Message.ShouldContain("gpt-99-not-listed");
    }

    [Fact]
    public void LoadFromStream_MissingDefaultModel_Throws()
    {
        const string json = """
        {
          "models": ["gpt-4o"],
          "defaultModel": "  "
        }
        """;

        Should.Throw<InvalidDataException>(() =>
            OpenAiAgentRuntimeSeedLoader.LoadFromStream(
                new MemoryStream(Encoding.UTF8.GetBytes(json)),
                sourceDescription: "<test>"));
    }

    [Fact]
    public void LoadFromStream_MalformedJson_Throws()
    {
        const string json = "this is not json";

        var ex = Should.Throw<InvalidDataException>(() =>
            OpenAiAgentRuntimeSeedLoader.LoadFromStream(
                new MemoryStream(Encoding.UTF8.GetBytes(json)),
                sourceDescription: "<test>"));

        ex.Message.ShouldContain("not valid JSON");
    }

    [Fact]
    public void LoadFromFile_MissingFile_ThrowsFileNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openai-seed-{Guid.NewGuid():N}.json");

        Should.Throw<FileNotFoundException>(() =>
            OpenAiAgentRuntimeSeedLoader.LoadFromFile(path));
    }

    [Fact]
    public void LoadFromFile_RoundTrips()
    {
        var seed = new OpenAiAgentRuntimeSeed(
            Models: new[] { "gpt-4o", "gpt-4o-mini" },
            DefaultModel: "gpt-4o",
            BaseUrl: "https://api.openai.com");

        var path = Path.Combine(Path.GetTempPath(), $"openai-seed-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(seed));

        try
        {
            var roundTripped = OpenAiAgentRuntimeSeedLoader.LoadFromFile(path);
            roundTripped.Models.ShouldBe(seed.Models);
            roundTripped.DefaultModel.ShouldBe(seed.DefaultModel);
            roundTripped.BaseUrl.ShouldBe(seed.BaseUrl);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromAssemblyDirectory_LoadsTheShippedSeed()
    {
        // The seed file is copied to the assembly directory by the
        // .csproj's <None CopyToOutputDirectory> entry. The runtime's
        // production loader expects to find it there — pin that
        // expectation as a test so a misconfigured project file fails
        // loudly.
        var seed = OpenAiAgentRuntimeSeedLoader.LoadFromAssemblyDirectory();

        seed.Models.ShouldNotBeEmpty();
        seed.Models.ShouldContain(seed.DefaultModel);
    }

    [Fact]
    public void ShippedSeed_MirrorsCuratedOpenAiList_FromIssue680()
    {
        // The runtime's seed encodes the curated OpenAI list the wizard
        // and CLI read at tenant-install time. Drift is intentional only
        // when a follow-up updates this test alongside the seed file.
        var seed = OpenAiAgentRuntimeSeedLoader.LoadFromAssemblyDirectory();

        seed.Models.ShouldBe(new[] { "gpt-4o", "gpt-4o-mini", "o3-mini" });
        seed.DefaultModel.ShouldBe("gpt-4o");
        seed.BaseUrl.ShouldBe("https://api.openai.com");
    }
}