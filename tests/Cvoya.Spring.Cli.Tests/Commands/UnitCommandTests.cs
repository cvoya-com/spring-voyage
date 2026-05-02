// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the free-function validators on
/// <see cref="UnitCommand"/>. The action pipelines themselves are
/// exercised end-to-end elsewhere — these tests pin the validation
/// contract so <c>spring unit create</c> and
/// <c>spring unit create-from-template</c> reject mis-composed flag
/// sets consistently (#598 + #644).
/// </summary>
public class UnitCommandTests
{
    // Canonical rejection message (#644) — operators read this verbatim
    // when they combine --provider / --model with a tool that doesn't
    // accept that flag.
    private const string ExpectedErrorMessage =
        "--provider is only meaningful for --tool=dapr-agent; " +
        "other tools (claude-code, codex, gemini) have their provider hardcoded in the tool CLI, " +
        "but accept --model to pick within that provider's model family.";

    [Theory]
    [InlineData("dapr-agent", "openai", "gpt-4o")]
    [InlineData("dapr-agent", "anthropic", null)]
    [InlineData("dapr-agent", null, "claude-sonnet-4-6")]
    [InlineData("dapr-agent", null, null)]
    public void ValidateProviderModelAgainstTool_DaprAgent_Accepts(
        string tool,
        string? provider,
        string? model)
    {
        UnitCommand.ValidateProviderModelAgainstTool(tool, provider, model)
            .ShouldBeNull();
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, null, "")]
    // No --tool supplied and no provider/model either — nothing to reject.
    [InlineData("", null, null)]
    public void ValidateProviderModelAgainstTool_NoFlags_Accepts(
        string? tool,
        string? provider,
        string? model)
    {
        UnitCommand.ValidateProviderModelAgainstTool(tool, provider, model)
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("claude-code", "anthropic", null)]
    [InlineData("claude-code", "anthropic", "claude-sonnet-4-6")]
    [InlineData("codex", "openai", "gpt-4o")]
    [InlineData("codex", "openai", null)]
    [InlineData("gemini", "google", "gemini-2.5-pro")]
    public void ValidateProviderModelAgainstTool_TooledProviderFlag_Rejected(
        string tool,
        string? provider,
        string? model)
    {
        // The tool hardcodes its provider in its own CLI — passing
        // --provider would silently be dropped at dispatch. Reject it
        // up-front with the canonical message so operators see the shape
        // of the contract instead of diagnosing a no-op.
        var error = UnitCommand.ValidateProviderModelAgainstTool(tool, provider, model);
        error.ShouldBe(ExpectedErrorMessage);
    }

    [Theory]
    // #644 parity fix: --model is meaningful for every tool that carries
    // a known provider family — the portal's wizard (PR #645) and
    // execution-tab (PR #643 follow-up) render the Model dropdown for
    // these tools, so the CLI must not be stricter than the portal.
    [InlineData("claude-code", "claude-sonnet-4-6")]
    [InlineData("claude-code", "claude-opus-4-7")]
    [InlineData("claude-code", "claude-haiku-4-5")]
    [InlineData("codex", "gpt-4o")]
    [InlineData("codex", "gpt-4o-mini")]
    [InlineData("gemini", "gemini-2.5-pro")]
    [InlineData("gemini", "gemini-2.5-flash")]
    // Opaque string we don't know — still accepted. Per (1) in #644 the
    // CLI treats model ids as opaque and defers validation to unit
    // activation on the server.
    [InlineData("claude-code", "something-that-does-not-exist-yet")]
    public void ValidateProviderModelAgainstTool_TooledModelFlag_Accepted(
        string tool,
        string model)
    {
        // No provider flag → no rejection. The tool provides the
        // provider internally; the operator's job is only to pick the
        // model inside that family.
        UnitCommand.ValidateProviderModelAgainstTool(tool, provider: null, model: model)
            .ShouldBeNull();
    }

    [Theory]
    // #644: --tool=custom has no declared provider / model contract, so
    // both flags are still rejected there (unchanged from the #598
    // behaviour).
    [InlineData("custom", "ollama", "llama3.2:3b")]
    [InlineData("custom", "ollama", null)]
    [InlineData("custom", null, "llama3.2:3b")]
    public void ValidateProviderModelAgainstTool_Custom_RejectsBoth(
        string tool,
        string? provider,
        string? model)
    {
        var error = UnitCommand.ValidateProviderModelAgainstTool(tool, provider, model);
        error.ShouldBe(ExpectedErrorMessage);
    }

    [Fact]
    public void ValidateProviderModelAgainstTool_CaseInsensitive_OnTool()
    {
        // The option's allow-list is lowercase but operators sometimes
        // type "Dapr-Agent"; the validator must normalise before the
        // check so they're not rejected for a casing accident.
        UnitCommand.ValidateProviderModelAgainstTool(
            "Dapr-Agent",
            provider: "openai",
            model: "gpt-4o")
            .ShouldBeNull();
    }

    [Fact]
    public void ValidateProviderModelAgainstTool_CaseInsensitive_OnTooledTool()
    {
        // Same normalisation for the tool-hardcoded-provider tools:
        // "Claude-Code" + --model is accepted just like "claude-code".
        UnitCommand.ValidateProviderModelAgainstTool(
            "Claude-Code",
            provider: null,
            model: "claude-sonnet-4-6")
            .ShouldBeNull();

        UnitCommand.ValidateProviderModelAgainstTool(
            "Claude-Code",
            provider: "anthropic",
            model: null)
            .ShouldBe(ExpectedErrorMessage);
    }

    [Fact]
    public void ValidateProviderModelAgainstTool_NoToolProvided_DoesNotSecondGuessServerDefault()
    {
        // When --tool is omitted the server picks the deployment default
        // (claude-code in today's build). The CLI must not assume that
        // default — rejecting --provider in that case would mean
        // operators who pin provider + model without passing --tool hit
        // a confusing error. The server already enforces the honest
        // contract at dispatch time.
        UnitCommand.ValidateProviderModelAgainstTool(
            tool: null,
            provider: "openai",
            model: "gpt-4o")
            .ShouldBeNull();
    }

    // -----------------------------------------------------------------
    // #626 / #742: inline credential flag resolution. #742 moves the
    // canonical secret-name lookup off a hardcoded client-side switch and
    // onto <c>GET /api/v1/agent-runtimes/{id}.credentialSecretName</c>;
    // the tests stub the resolver with the canonical mapping so we can
    // still pin rejection semantics without standing up an API.
    // -----------------------------------------------------------------

    // Canonical { runtime-id → secretName } shape the agent-runtime API
    // returns today. Kept in lock-step with each runtime's
    // `IAgentRuntime.CredentialSecretName` on the server so the stub
    // faithfully mimics a healthy install.
    private static Func<string, CancellationToken, Task<string?>> StubRuntimeSecretNameResolver(
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claude"] = "anthropic-api-key",
            ["openai"] = "openai-api-key",
            ["google"] = "google-api-key",
            ["ollama"] = string.Empty,
        };
        if (overrides is not null)
        {
            foreach (var (k, v) in overrides)
            {
                canonical[k] = v;
            }
        }
        return (runtimeId, _) => Task.FromResult<string?>(
            canonical.TryGetValue(runtimeId, out var name) ? name : null);
    }

    [Theory]
    [InlineData("claude-code", null, "claude")]
    [InlineData("codex", null, "openai")]
    [InlineData("gemini", null, "google")]
    [InlineData("dapr-agent", "anthropic", "claude")]
    [InlineData("dapr-agent", "claude", "claude")]
    [InlineData("dapr-agent", "openai", "openai")]
    [InlineData("dapr-agent", "google", "google")]
    [InlineData("dapr-agent", "gemini", "google")]
    [InlineData("dapr-agent", "ollama", "ollama")]
    [InlineData("dapr-agent", "unknown", null)]
    [InlineData("custom", "openai", null)]
    [InlineData(null, null, null)]
    public void DeriveRequiredRuntimeId_MatchesMatrix(
        string? tool,
        string? provider,
        string? expected)
    {
        UnitCommand.DeriveRequiredRuntimeId(tool, provider)
            .ShouldBe(expected);
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_NoFlags_ReturnsNone()
    {
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            tool: "claude-code",
            provider: null,
            apiKey: null,
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage.ShouldBeNull();
        result.SecretName.ShouldBeNull();
        result.Key.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsBothKeyFlagsTogether()
    {
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            tool: "claude-code",
            provider: null,
            apiKey: "sk-test",
            apiKeyFromFile: "some-path",
            saveAsTenantDefault: false,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("mutually exclusive");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsSaveFlagWithoutKey()
    {
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            tool: "claude-code",
            provider: null,
            apiKey: null,
            apiKeyFromFile: null,
            saveAsTenantDefault: true,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("--save-as-tenant-default requires");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsKeyOnOllamaProvider()
    {
        // Ollama maps to a registered runtime (`ollama`) whose
        // CredentialSecretName is the empty string — the resolver
        // surfaces that as "no credential to write".
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            tool: "dapr-agent",
            provider: "ollama",
            apiKey: "sk-test",
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("no credential");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsKeyOnCustomTool()
    {
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            tool: "custom",
            provider: null,
            apiKey: "sk-test",
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("custom");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsWhenRuntimeNotInstalled()
    {
        // Runtime maps cleanly (`claude`) but the resolver returns null —
        // i.e. `GET /api/v1/agent-runtimes/claude` would 404. Surface a
        // clear message pointing at `spring agent-runtime install` so
        // the operator knows the remedy.
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            tool: "claude-code",
            provider: null,
            apiKey: "sk-ant",
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            (_, _) => Task.FromResult<string?>(null),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("not installed");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_AcceptsInlineKey_ClaudeCode()
    {
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            tool: "claude-code",
            provider: null,
            apiKey: "sk-ant-xyz",
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage.ShouldBeNull();
        result.Key.ShouldBe("sk-ant-xyz");
        result.SecretName.ShouldBe("anthropic-api-key");
        result.SaveAsTenantDefault.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_AcceptsSaveAsTenantDefaultToggle()
    {
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            tool: "codex",
            provider: null,
            apiKey: "sk-openai",
            apiKeyFromFile: null,
            saveAsTenantDefault: true,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage.ShouldBeNull();
        result.Key.ShouldBe("sk-openai");
        result.SecretName.ShouldBe("openai-api-key");
        result.SaveAsTenantDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_ReadsKeyFromFile_StripsTrailingNewline()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "sk-file-key\n", TestContext.Current.CancellationToken);
            var result = await UnitCommand.ResolveCredentialOptionsAsync(
                tool: "gemini",
                provider: null,
                apiKey: null,
                apiKeyFromFile: path,
                saveAsTenantDefault: false,
                StubRuntimeSecretNameResolver(),
                TestContext.Current.CancellationToken);
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
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            tool: "claude-code",
            provider: null,
            apiKey: null,
            apiKeyFromFile: "/tmp/does-not-exist-please-really",
            saveAsTenantDefault: false,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("Failed to read");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsEmptyKey()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "\n\n", TestContext.Current.CancellationToken);
            var result = await UnitCommand.ResolveCredentialOptionsAsync(
                tool: "claude-code",
                provider: null,
                apiKey: null,
                apiKeyFromFile: path,
                saveAsTenantDefault: false,
                StubRuntimeSecretNameResolver(),
                TestContext.Current.CancellationToken);
            result.ErrorMessage!.ShouldContain("empty");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RespectsRuntimeSecretNameOverride()
    {
        // If a downstream tenant (or a custom runtime in the private
        // repo) stores the credential under a different secret name,
        // the API-returned value wins over any client-side assumption.
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            tool: "claude-code",
            provider: null,
            apiKey: "sk-ant-xyz",
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            StubRuntimeSecretNameResolver(new Dictionary<string, string>
            {
                ["claude"] = "custom-claude-key",
            }),
            CancellationToken.None);
        result.ErrorMessage.ShouldBeNull();
        result.SecretName.ShouldBe("custom-claude-key");
    }

}