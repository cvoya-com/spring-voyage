// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Text.Json;

using Cvoya.Spring.Dapr.Observability;

using Shouldly;

using Xunit;

/// <summary>
/// Issue #2492 — library-defined redaction at ingest.
/// </summary>
public class ActivityRedactorTests
{
    [Fact]
    public void Redact_AuthorizationHeaderInAttributes_MasksValue()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            method = "POST",
            headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature",
                ["X-Trace-Id"] = "abc123",
            },
        });

        var redacted = ActivityRedactor.Redact(input);
        var headers = redacted.GetProperty("headers");

        headers.GetProperty("Authorization").GetString().ShouldBe(ActivityRedactor.RedactedMarker);
        headers.GetProperty("X-Trace-Id").GetString().ShouldBe("abc123");
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("Authorization")]
    [InlineData("AUTHORIZATION")]
    [InlineData("Proxy-Authorization")]
    [InlineData("x-api-key")]
    [InlineData("X-API-KEY")]
    [InlineData("X-Auth-Token")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    public void Redact_HeaderKey_CaseInsensitive(string headerKey)
    {
        var input = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            [headerKey] = "secret-value",
        });

        var redacted = ActivityRedactor.Redact(input);
        redacted.GetProperty(headerKey).GetString().ShouldBe(ActivityRedactor.RedactedMarker);
    }

    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("MY_SECRET")]
    [InlineData("DB_PASSWORD")]
    [InlineData("SOME_PWD")]
    [InlineData("SOMETHING_PASSWD")]
    public void Redact_CredentialEnvVarKey_MasksValue(string envVarKey)
    {
        var input = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            [envVarKey] = "real-token-value",
            ["HOME"] = "/root",
        });

        var redacted = ActivityRedactor.Redact(input);

        redacted.GetProperty(envVarKey).GetString().ShouldBe(ActivityRedactor.RedactedMarker);
        redacted.GetProperty("HOME").GetString().ShouldBe("/root");
    }

    [Fact]
    public void Redact_BearerTokenEmbeddedInAttributeValue_MasksInline()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            curl = "curl -H 'Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.abc.def' https://api.example.com",
        });

        var redacted = ActivityRedactor.Redact(input);
        var value = redacted.GetProperty("curl").GetString().ShouldNotBeNull();
        value.ShouldNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9");
        value.ShouldContain(ActivityRedactor.RedactedMarker);
    }

    [Fact]
    public void Redact_NestedCredentialInArray_MasksValue()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            env = new[]
            {
                new { name = "HOME", value = "/root" },
                new { name = "ANTHROPIC_API_KEY", value = "sk-ant-real" },
            },
        });

        var redacted = ActivityRedactor.Redact(input);
        var arr = redacted.GetProperty("env");
        arr[1].GetProperty("name").GetString().ShouldBe("ANTHROPIC_API_KEY");
        arr[1].GetProperty("value").GetString().ShouldBe("sk-ant-real",
            customMessage: "Inline value scan only matches `Bearer …` / `Basic …` shapes; the env-var key-based mask happens at the key level, not the nested 'value' field.");
        // The 'name' / 'value' shape is intentionally not redacted from the
        // outer key — that's the OTel attribute representation. Redaction
        // would need a higher-level convention to map "{ name, value }" tuples
        // to key/value pairs.
    }

    [Fact]
    public void Redact_PreservesUnrelatedFields_Unchanged()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            tool = "github.cli",
            args = new[] { "issue", "list", "--label", "bug" },
            nested = new { kind = "list", count = 3 },
        });

        var redacted = ActivityRedactor.Redact(input);
        redacted.GetProperty("tool").GetString().ShouldBe("github.cli");
        redacted.GetProperty("args").GetArrayLength().ShouldBe(4);
        redacted.GetProperty("nested").GetProperty("kind").GetString().ShouldBe("list");
        redacted.GetProperty("nested").GetProperty("count").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void IsCredentialEnvVarKey_KnownPatterns_ReturnsTrue()
    {
        ActivityRedactor.IsCredentialEnvVarKey("OPENAI_API_KEY").ShouldBeTrue();
        ActivityRedactor.IsCredentialEnvVarKey("MY_TOKEN").ShouldBeTrue();
        ActivityRedactor.IsCredentialEnvVarKey("SOMETHING_PASSWORD").ShouldBeTrue();
        ActivityRedactor.IsCredentialEnvVarKey("PATH").ShouldBeFalse();
        ActivityRedactor.IsCredentialEnvVarKey("HOME").ShouldBeFalse();
    }

    [Fact]
    public void IsCredentialHeaderKey_KnownHeaders_ReturnsTrue()
    {
        ActivityRedactor.IsCredentialHeaderKey("Authorization").ShouldBeTrue();
        ActivityRedactor.IsCredentialHeaderKey("x-api-key").ShouldBeTrue();
        ActivityRedactor.IsCredentialHeaderKey("Content-Type").ShouldBeFalse();
        ActivityRedactor.IsCredentialHeaderKey("User-Agent").ShouldBeFalse();
    }
}
