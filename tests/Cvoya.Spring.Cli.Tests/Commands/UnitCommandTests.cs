// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the inline-credential resolver on <see cref="UnitCommand"/>.
/// The action pipelines themselves are exercised end-to-end elsewhere; these
/// tests pin the resolver contract that <c>spring unit create</c> threads
/// through before writing tenant/unit secrets.
/// </summary>
public class UnitCommandTests
{
    private static Func<string, CancellationToken, Task<bool>> StubModelProviderInstalledResolver(
        params string[] missingProviders)
    {
        var missing = new HashSet<string>(missingProviders, StringComparer.Ordinal);
        return (providerId, _) => Task.FromResult(!missing.Contains(providerId));
    }

    private static Task<UnitCredentialOptions> ResolveAsync(
        string? runtimeId = "claude-code",
        string? modelProviderId = null,
        string? apiKey = null,
        string? apiKeyFromFile = null,
        string? oauthToken = null,
        string? oauthTokenFromFile = null,
        bool saveAsTenantDefault = false,
        Func<string, CancellationToken, Task<bool>>? modelProviderInstalledResolver = null)
    {
        return UnitCommand.ResolveCredentialOptionsAsync(
            runtimeId,
            modelProviderId,
            apiKey,
            apiKeyFromFile,
            oauthToken,
            oauthTokenFromFile,
            saveAsTenantDefault,
            modelProviderInstalledResolver ?? StubModelProviderInstalledResolver(),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_NoFlags_ReturnsNone()
    {
        var result = await ResolveAsync();

        result.ErrorMessage.ShouldBeNull();
        result.SecretName.ShouldBeNull();
        result.Key.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsBothApiKeyFlagsTogether()
    {
        var result = await ResolveAsync(
            apiKey: "sk-test",
            apiKeyFromFile: "some-path");

        result.ErrorMessage!.ShouldContain("mutually exclusive");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsBothOAuthTokenFlagsTogether()
    {
        var result = await ResolveAsync(
            oauthToken: "oauth-token",
            oauthTokenFromFile: "some-path");

        result.ErrorMessage!.ShouldContain("mutually exclusive");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsMixedCredentialFamilies()
    {
        var result = await ResolveAsync(
            apiKey: "sk-test",
            oauthToken: "oauth-token");

        result.ErrorMessage!.ShouldContain("API-key and OAuth-token flags");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsSaveFlagWithoutCredential()
    {
        var result = await ResolveAsync(saveAsTenantDefault: true);

        result.ErrorMessage!.ShouldContain("--save-as-tenant-default requires");
        result.ErrorMessage!.ShouldContain("--oauth-token");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsCredentialWithoutRuntime()
    {
        var result = await ResolveAsync(runtimeId: null, apiKey: "sk-test");

        result.ErrorMessage!.ShouldContain("--runtime");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsApiKeyOnClaudeCodeRuntime()
    {
        var result = await ResolveAsync(apiKey: "sk-ant-api-test");

        result.ErrorMessage!.ShouldContain("CLAUDE_CODE_OAUTH_TOKEN");
        result.ErrorMessage!.ShouldContain("--oauth-token");
        result.ErrorMessage!.ShouldContain("claude setup-token");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_AcceptsOAuthTokenForClaudeCodeRuntime()
    {
        var result = await ResolveAsync(oauthToken: "oauth-token-value");

        result.ErrorMessage.ShouldBeNull();
        result.Key.ShouldBe("oauth-token-value");
        result.SecretName.ShouldBe("anthropic-oauth");
        result.AuthMethod.ShouldBe(Cvoya.Spring.Core.Catalog.AuthMethod.Oauth);
        result.CredentialEnvVar.ShouldBe("CLAUDE_CODE_OAUTH_TOKEN");
        result.SaveAsTenantDefault.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_AcceptsOAuthTokenFileForClaudeCodeRuntime()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "oauth-token-value\n", TestContext.Current.CancellationToken);

            var result = await ResolveAsync(
                oauthTokenFromFile: path);

            result.ErrorMessage.ShouldBeNull();
            result.Key.ShouldBe("oauth-token-value");
            result.SecretName.ShouldBe("anthropic-oauth");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("codex", null, "openai-api-key", "OPENAI_API_KEY")]
    [InlineData("gemini", null, "google-api-key", "GOOGLE_API_KEY")]
    [InlineData("spring-voyage", "anthropic", "anthropic-api-key", "ANTHROPIC_API_KEY")]
    [InlineData("spring-voyage", "openai", "openai-api-key", "OPENAI_API_KEY")]
    [InlineData("spring-voyage", "google", "google-api-key", "GOOGLE_API_KEY")]
    public async Task ResolveCredentialOptionsAsync_AcceptsApiKeyEdges(
        string runtimeId,
        string? modelProviderId,
        string expectedSecretName,
        string expectedEnvVar)
    {
        var result = await ResolveAsync(
            runtimeId: runtimeId,
            modelProviderId: modelProviderId,
            apiKey: "api-key-value",
            saveAsTenantDefault: true);

        result.ErrorMessage.ShouldBeNull();
        result.Key.ShouldBe("api-key-value");
        result.SecretName.ShouldBe(expectedSecretName);
        result.AuthMethod.ShouldBe(Cvoya.Spring.Core.Catalog.AuthMethod.ApiKey);
        result.CredentialEnvVar.ShouldBe(expectedEnvVar);
        result.SaveAsTenantDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsOAuthTokenOnApiKeyEdge()
    {
        var result = await ResolveAsync(
            runtimeId: "codex",
            oauthToken: "oauth-token");

        result.ErrorMessage!.ShouldContain("requires an API key");
        result.ErrorMessage!.ShouldContain("--api-key");
        result.ErrorMessage!.ShouldContain("OPENAI_API_KEY");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsCredentialOnOllamaEdge()
    {
        var result = await ResolveAsync(
            runtimeId: "spring-voyage",
            modelProviderId: "ollama",
            apiKey: "anything");

        result.ErrorMessage!.ShouldContain("declares no credential");
        result.ErrorMessage!.ShouldContain("--oauth-token");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RequiresProviderForMultiProviderRuntime()
    {
        var result = await ResolveAsync(
            runtimeId: "spring-voyage",
            apiKey: "api-key-value");

        result.ErrorMessage!.ShouldContain("--model-provider");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsWrongProviderForFixedRuntime()
    {
        var result = await ResolveAsync(
            runtimeId: "claude-code",
            modelProviderId: "openai",
            oauthToken: "oauth-token");

        result.ErrorMessage!.ShouldContain("fixed to provider 'anthropic'");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsWhenProviderNotInstalled()
    {
        var result = await ResolveAsync(
            runtimeId: "codex",
            apiKey: "sk-openai",
            modelProviderInstalledResolver: StubModelProviderInstalledResolver("openai"));

        result.ErrorMessage!.ShouldContain("not installed");
        result.ErrorMessage!.ShouldContain("spring model-provider install openai");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_ReadsApiKeyFromFile_StripsTrailingNewline()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "sk-file-key\n", TestContext.Current.CancellationToken);

            var result = await ResolveAsync(
                runtimeId: "gemini",
                apiKeyFromFile: path);

            result.ErrorMessage.ShouldBeNull();
            result.Key.ShouldBe("sk-file-key");
            result.SecretName.ShouldBe("google-api-key");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsMissingFile()
    {
        var result = await ResolveAsync(
            runtimeId: "codex",
            apiKeyFromFile: "/tmp/does-not-exist-please-really");

        result.ErrorMessage!.ShouldContain("Failed to read --api-key-from-file");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsEmptyCredential()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "\n\n", TestContext.Current.CancellationToken);

            var result = await ResolveAsync(
                oauthTokenFromFile: path);

            result.ErrorMessage!.ShouldContain("OAuth token is empty");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
