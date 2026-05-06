// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the inline-credential resolver on
/// <see cref="UnitCommand"/>. The action pipelines themselves are exercised
/// end-to-end elsewhere; these tests pin the resolver contract that
/// <c>spring unit create --api-key</c> threads through.
/// </summary>
/// <remarks>
/// #1732: <c>--tool</c> was dropped — operators name the runtime via
/// <c>--agent</c>, and the resolver routes the credential write to that
/// runtime's <c>CredentialSecretName</c>. The tool-to-runtime bridge
/// (<c>DeriveRequiredRuntimeId</c>) and the
/// <c>ValidateProviderModelAgainstTool</c> validator are gone.
/// </remarks>
public class UnitCommandTests
{
    // -----------------------------------------------------------------
    // #626 / #742: inline credential flag resolution. #742 moves the
    // canonical secret-name lookup off a hardcoded client-side switch and
    // onto <c>GET /api/v1/agent-runtimes/{id}.credentialSecretName</c>;
    // the tests stub the resolver with the canonical mapping so we can
    // still pin rejection semantics without standing up an API.
    // #1732: the resolver now takes a runtime id directly (--agent), not a
    // tool + provider pair.
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

    [Fact]
    public async Task ResolveCredentialOptionsAsync_NoFlags_ReturnsNone()
    {
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            agentRuntimeId: "claude",
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
            agentRuntimeId: "claude",
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
            agentRuntimeId: "claude",
            apiKey: null,
            apiKeyFromFile: null,
            saveAsTenantDefault: true,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("--save-as-tenant-default requires");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsKeyOnOllamaRuntime()
    {
        // Ollama's CredentialSecretName is the empty string — the resolver
        // surfaces that as "no credential to write".
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            agentRuntimeId: "ollama",
            apiKey: "sk-test",
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("no credential");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsKeyWithoutAgent()
    {
        // #1732: --agent is required when an inline key is supplied — the
        // CLI no longer infers the runtime from --tool.
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            agentRuntimeId: null,
            apiKey: "sk-test",
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("--agent");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsWhenRuntimeNotInstalled()
    {
        // Runtime maps cleanly (`claude`) but the resolver returns null —
        // i.e. `GET /api/v1/agent-runtimes/claude` would 404. Surface a
        // clear message pointing at `spring agent-runtime install` so
        // the operator knows the remedy.
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            agentRuntimeId: "claude",
            apiKey: "sk-ant",
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            (_, _) => Task.FromResult<string?>(null),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("not installed");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_AcceptsInlineKey_ClaudeRuntime()
    {
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            agentRuntimeId: "claude",
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
            agentRuntimeId: "openai",
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
                agentRuntimeId: "google",
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
            agentRuntimeId: "claude",
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
                agentRuntimeId: "claude",
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
            agentRuntimeId: "claude",
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