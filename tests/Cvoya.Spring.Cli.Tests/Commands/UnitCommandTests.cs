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
/// ADR-0038: operators name the runtime via <c>--runtime</c>, and the
/// resolver routes the credential write to the runtime's
/// <c>credentialSecretName</c>. The host translates runtime → provider
/// internally; the CLI surfaces only the runtime id.
/// </remarks>
public class UnitCommandTests
{
    // -----------------------------------------------------------------
    // Inline credential flag resolution. The canonical secret-name lookup
    // lives behind <c>GET /api/v1/tenant/model-providers/installs/{id}</c>;
    // these tests stub the resolver with a faithful canonical mapping so
    // we can pin rejection semantics without standing up an API. ADR-0038
    // re-keys the resolver on the runtime id (--runtime).
    // -----------------------------------------------------------------

    // Canonical { runtime-id → secretName } shape the model-provider API
    // returns today. Kept in lock-step with each runtime's
    // credential secret name on the server so the stub faithfully mimics
    // a healthy install.
    private static Func<string, CancellationToken, Task<string?>> StubRuntimeSecretNameResolver(
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claude-code"] = "anthropic-api-key",
            ["codex"] = "openai-api-key",
            ["gemini"] = "google-api-key",
            ["spring-voyage"] = "anthropic-api-key",
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
            runtimeId: "claude-code",
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
            runtimeId: "claude-code",
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
            runtimeId: "claude-code",
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
        // Ollama's credential secret name is the empty string — the resolver
        // surfaces that as "no credential to write".
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            runtimeId: "ollama",
            apiKey: "sk-test",
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("no credential");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsKeyWithoutRuntime()
    {
        // ADR-0038: --runtime is required when an inline key is supplied —
        // the resolver routes the write through the runtime → provider edge.
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            runtimeId: null,
            apiKey: "sk-test",
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            StubRuntimeSecretNameResolver(),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("--runtime");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_RejectsWhenRuntimeNotInstalled()
    {
        // Runtime id maps cleanly (`claude-code`) but the resolver returns
        // null — i.e. the matching model-provider install would 404.
        // Surface a clear message pointing at `spring model-provider install`
        // so the operator knows the remedy.
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            runtimeId: "claude-code",
            apiKey: "sk-ant",
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            (_, _) => Task.FromResult<string?>(null),
            CancellationToken.None);
        result.ErrorMessage!.ShouldContain("not installed");
        result.ErrorMessage!.ShouldContain("spring model-provider install");
    }

    [Fact]
    public async Task ResolveCredentialOptionsAsync_AcceptsInlineKey_ClaudeCodeRuntime()
    {
        var result = await UnitCommand.ResolveCredentialOptionsAsync(
            runtimeId: "claude-code",
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
            runtimeId: "codex",
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
                runtimeId: "gemini",
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
            runtimeId: "claude-code",
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
                runtimeId: "claude-code",
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
            runtimeId: "claude-code",
            apiKey: "sk-ant-xyz",
            apiKeyFromFile: null,
            saveAsTenantDefault: false,
            StubRuntimeSecretNameResolver(new Dictionary<string, string>
            {
                ["claude-code"] = "custom-claude-key",
            }),
            CancellationToken.None);
        result.ErrorMessage.ShouldBeNull();
        result.SecretName.ShouldBe("custom-claude-key");
    }
}
