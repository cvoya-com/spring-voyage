// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Google.Tests;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.AgentRuntimes.Google;

using Shouldly;

using Xunit;

public class GoogleAgentRuntimeSeedTests
{
    [Fact]
    public void LoadFromStream_FullPayload_DeserialisesEveryField()
    {
        const string json = """
        {
          "models": ["gemini-2.5-pro", "gemini-2.5-flash"],
          "defaultModel": "gemini-2.5-pro",
          "baseUrl": "https://generativelanguage.googleapis.com"
        }
        """;

        var seed = LoadFromString(json);

        seed.Models.ShouldBe(new[] { "gemini-2.5-pro", "gemini-2.5-flash" });
        seed.DefaultModel.ShouldBe("gemini-2.5-pro");
        seed.BaseUrl.ShouldBe("https://generativelanguage.googleapis.com");
    }

    [Fact]
    public void LoadFromStream_OnlyRequiredFields_TolerateMissingBaseUrl()
    {
        const string json = """
        {
          "models": ["gemini-2.5-pro"],
          "defaultModel": "gemini-2.5-pro"
        }
        """;

        var seed = LoadFromString(json);

        seed.Models.ShouldBe(new[] { "gemini-2.5-pro" });
        seed.DefaultModel.ShouldBe("gemini-2.5-pro");
        seed.BaseUrl.ShouldBeNull();
    }

    [Fact]
    public void LoadFromStream_RoundTrip_PreservesFields()
    {
        var original = new GoogleAgentRuntimeSeed(
            Models: new[] { "gemini-2.5-pro", "gemini-2.5-flash" },
            DefaultModel: "gemini-2.5-pro",
            BaseUrl: "https://example.test");

        var json = JsonSerializer.Serialize(original, GetSerialiserOptions());
        var roundTripped = LoadFromString(json);

        roundTripped.Models.ShouldBe(original.Models);
        roundTripped.DefaultModel.ShouldBe(original.DefaultModel);
        roundTripped.BaseUrl.ShouldBe(original.BaseUrl);
    }

    [Fact]
    public void LoadFromStream_MalformedJson_Throws_InvalidDataException()
    {
        const string json = "{ not valid json";

        Should.Throw<InvalidDataException>(() => LoadFromString(json));
    }

    [Fact]
    public void LoadFromStream_EmptyModels_Throws_InvalidDataException()
    {
        const string json = """{ "models": [], "defaultModel": "gemini-2.5-pro" }""";

        var ex = Should.Throw<InvalidDataException>(() => LoadFromString(json));
        ex.Message.ShouldContain("models");
    }

    [Fact]
    public void LoadFromStream_BlankDefaultModel_Throws_InvalidDataException()
    {
        const string json = """{ "models": ["gemini-2.5-pro"], "defaultModel": "" }""";

        var ex = Should.Throw<InvalidDataException>(() => LoadFromString(json));
        ex.Message.ShouldContain("defaultModel");
    }

    [Fact]
    public void LoadFromStream_DefaultModelNotInModels_Throws_InvalidDataException()
    {
        const string json = """
        {
          "models": ["gemini-2.5-pro"],
          "defaultModel": "gemini-1.5-pro"
        }
        """;

        var ex = Should.Throw<InvalidDataException>(() => LoadFromString(json));
        ex.Message.ShouldContain("defaultModel");
        ex.Message.ShouldContain("gemini-1.5-pro");
    }

    [Fact]
    public void LoadFromFile_MissingFile_Throws_FileNotFoundException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"non-existent-seed-{Guid.NewGuid():N}.json");

        Should.Throw<FileNotFoundException>(() => GoogleAgentRuntimeSeedLoader.LoadFromFile(path));
    }

    [Fact]
    public void LoadFromAssemblyDirectory_ShipsCuratedGoogleList()
    {
        // The seed file ships in the runtime project's output directory and
        // is the source of truth for the runtime's DefaultModels. Pin the
        // shipped content so a typo in seed.json fails the build rather
        // than silently breaking the wizard's model picker.
        var seed = GoogleAgentRuntimeSeedLoader.LoadFromAssemblyDirectory();

        seed.Models.ShouldBe(new[] { "gemini-2.5-pro", "gemini-2.5-flash" });
        seed.DefaultModel.ShouldBe("gemini-2.5-pro");
        seed.BaseUrl.ShouldBe("https://generativelanguage.googleapis.com");
    }

    private static GoogleAgentRuntimeSeed LoadFromString(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return GoogleAgentRuntimeSeedLoader.LoadFromStream(stream, "test-payload");
    }

    private static JsonSerializerOptions GetSerialiserOptions() => new(JsonSerializerDefaults.Web);
}