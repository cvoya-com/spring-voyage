// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.AgentRuntimes;

using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.AgentRuntimes;

using Shouldly;

using Xunit;

public class AgentRuntimeContractTests
{
    [Fact]
    public void CredentialValidationResult_Valid_CarriesStatus()
    {
        var result = new CredentialValidationResult(true, null, CredentialValidationStatus.Valid);

        result.Valid.ShouldBeTrue();
        result.ErrorMessage.ShouldBeNull();
        result.Status.ShouldBe(CredentialValidationStatus.Valid);
    }

    [Fact]
    public void CredentialValidationResult_NetworkError_CarriesMessage()
    {
        var result = new CredentialValidationResult(false, "timeout", CredentialValidationStatus.NetworkError);

        result.Valid.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("timeout");
        result.Status.ShouldBe(CredentialValidationStatus.NetworkError);
    }

    [Fact]
    public void AgentRuntimeCredentialSchema_DefaultsDisplayHintToNull()
    {
        var schema = new AgentRuntimeCredentialSchema(AgentRuntimeCredentialKind.ApiKey);

        schema.Kind.ShouldBe(AgentRuntimeCredentialKind.ApiKey);
        schema.DisplayHint.ShouldBeNull();
    }

    [Fact]
    public void ModelDescriptor_AllowsNullContextWindow()
    {
        var model = new ModelDescriptor("m1", "Model One", null);

        model.Id.ShouldBe("m1");
        model.DisplayName.ShouldBe("Model One");
        model.ContextWindow.ShouldBeNull();
    }

    [Fact]
    public void ContainerBaselineCheckResult_Passed_HasEmptyErrors()
    {
        var result = new ContainerBaselineCheckResult(true, Array.Empty<string>());

        result.Passed.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void ContainerBaselineCheckResult_Failed_CarriesErrors()
    {
        var result = new ContainerBaselineCheckResult(false, new[] { "missing binary: claude" });

        result.Passed.ShouldBeFalse();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].ShouldBe("missing binary: claude");
    }
}

/// <summary>
/// Round-trip tests for the agent-runtime seed file schema documented at
/// <c>src/Cvoya.Spring.Core/AgentRuntimes/README.md</c>. The contract itself
/// does not load seeds — each runtime owns its own loading logic — but the
/// schema is stable, so we pin it here so drift shows up as a test failure.
/// </summary>
public class AgentRuntimeSeedSchemaTests
{
    /// <summary>
    /// Mirror of the documented seed schema:
    /// <c>{ "models": string[], "defaultModel": string, "baseUrl"?: string, "extras"?: object }</c>.
    /// </summary>
    private sealed record SeedFile(
        [property: JsonPropertyName("models")] IReadOnlyList<string> Models,
        [property: JsonPropertyName("defaultModel")] string DefaultModel,
        [property: JsonPropertyName("baseUrl")] string? BaseUrl = null,
        [property: JsonPropertyName("extras")] JsonElement? Extras = null);

    [Fact]
    public void Seed_RoundTrips_WithAllFields()
    {
        const string json = """
        {
          "models": ["claude-sonnet-4-5", "claude-haiku-4-5"],
          "defaultModel": "claude-sonnet-4-5",
          "baseUrl": "https://api.anthropic.com",
          "extras": { "requiresCli": true }
        }
        """;

        var seed = JsonSerializer.Deserialize<SeedFile>(json);

        seed.ShouldNotBeNull();
        seed!.Models.Count.ShouldBe(2);
        seed.Models[0].ShouldBe("claude-sonnet-4-5");
        seed.DefaultModel.ShouldBe("claude-sonnet-4-5");
        seed.BaseUrl.ShouldBe("https://api.anthropic.com");
        seed.Extras.ShouldNotBeNull();
        seed.Extras!.Value.GetProperty("requiresCli").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Seed_RoundTrips_WithOnlyRequiredFields()
    {
        const string json = """
        {
          "models": ["gpt-4o-mini"],
          "defaultModel": "gpt-4o-mini"
        }
        """;

        var seed = JsonSerializer.Deserialize<SeedFile>(json);

        seed.ShouldNotBeNull();
        seed!.Models.ShouldBe(new[] { "gpt-4o-mini" });
        seed.DefaultModel.ShouldBe("gpt-4o-mini");
        seed.BaseUrl.ShouldBeNull();
        seed.Extras.ShouldBeNull();
    }

    [Fact]
    public void Seed_SerializeThenDeserialize_Preserves_Fields()
    {
        var original = new SeedFile(
            Models: new[] { "m1", "m2" },
            DefaultModel: "m1",
            BaseUrl: "http://localhost:11434",
            Extras: JsonDocument.Parse("""{ "stream": true }""").RootElement);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<SeedFile>(json);

        roundTripped.ShouldNotBeNull();
        roundTripped!.Models.ShouldBe(original.Models);
        roundTripped.DefaultModel.ShouldBe(original.DefaultModel);
        roundTripped.BaseUrl.ShouldBe(original.BaseUrl);
        roundTripped.Extras.ShouldNotBeNull();
        roundTripped.Extras!.Value.GetProperty("stream").GetBoolean().ShouldBeTrue();
    }
}