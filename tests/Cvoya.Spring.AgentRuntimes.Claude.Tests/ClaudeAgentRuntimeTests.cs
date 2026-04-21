// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Claude.Tests;

using System.Net;
using System.Text.Json;

using Cvoya.Spring.AgentRuntimes.Claude.Internal;
using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Behaviour tests for <see cref="ClaudeAgentRuntime"/>. Covers the
/// split-brain credential validation (#660 logic preserved), the REST
/// fallback path for API keys when the CLI is missing, and the
/// container-baseline probe that closes #668.
/// </summary>
public class ClaudeAgentRuntimeTests
{
    [Fact]
    public void Identity_MatchesContract()
    {
        var runtime = CreateRuntime(out _, out _);

        runtime.Id.ShouldBe("claude");
        runtime.ToolKind.ShouldBe("claude-code-cli");
        runtime.DisplayName.ShouldBe("Claude (Claude Code CLI + Anthropic API)");
        runtime.CredentialSchema.Kind.ShouldBe(AgentRuntimeCredentialKind.ApiKey);
        runtime.CredentialSchema.DisplayHint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DefaultModels_LoadFromEmbeddedSeed()
    {
        var runtime = CreateRuntime(out _, out _);

        runtime.DefaultModels.Count.ShouldBeGreaterThan(0);
        var ids = runtime.DefaultModels.Select(m => m.Id).ToArray();
        ids.ShouldContain("claude-sonnet-4-20250514");
        ids.ShouldContain("claude-opus-4-20250514");
        ids.ShouldContain("claude-haiku-4-20250514");
        runtime.DefaultBaseUrl.ShouldBe("https://api.anthropic.com");
    }

    // --- Credential validation: API keys via the CLI ---

    [Fact]
    public async Task ValidateCredentialAsync_ApiKey_CliPresent_PlumbsThroughApiKeyEnvVar()
    {
        var runtime = CreateRuntime(out var runner, out _);
        runner.EnqueueBaselineSuccess();
        runner.EnqueueSuccess(new ProcessRunResult(
            0,
            JsonSerializer.Serialize(new { type = "result", is_error = false, result = "OK" }),
            string.Empty));

        var result = await runtime.ValidateCredentialAsync("sk-ant-api03-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Valid);
        result.Valid.ShouldBeTrue();
        var env = runner.LastEnvironment.ShouldNotBeNull();
        env.ShouldContainKeyAndValue("ANTHROPIC_API_KEY", "sk-ant-api03-key");
        env.ShouldNotContainKey("CLAUDE_CODE_OAUTH_TOKEN");
    }

    [Fact]
    public async Task ValidateCredentialAsync_OAuthToken_CliPresent_PlumbsThroughOAuthEnvVar()
    {
        var runtime = CreateRuntime(out var runner, out _);
        runner.EnqueueBaselineSuccess();
        runner.EnqueueSuccess(new ProcessRunResult(
            0,
            JsonSerializer.Serialize(new { type = "result", is_error = false, result = "OK" }),
            string.Empty));

        var result = await runtime.ValidateCredentialAsync("sk-ant-oat01-tok", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Valid);
        var env = runner.LastEnvironment.ShouldNotBeNull();
        env.ShouldContainKeyAndValue("CLAUDE_CODE_OAUTH_TOKEN", "sk-ant-oat01-tok");
        env.ShouldNotContainKey("ANTHROPIC_API_KEY");
    }

    [Fact]
    public async Task ValidateCredentialAsync_CliReports401_MapsToInvalid()
    {
        var runtime = CreateRuntime(out var runner, out _);
        runner.EnqueueBaselineSuccess();
        runner.EnqueueSuccess(new ProcessRunResult(
            0,
            JsonSerializer.Serialize(new { type = "result", is_error = true, api_error_status = 401, result = "bad key" }),
            string.Empty));

        var result = await runtime.ValidateCredentialAsync("sk-ant-api03-bad", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Invalid);
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ValidateCredentialAsync_CliReports5xx_MapsToNetworkError()
    {
        var runtime = CreateRuntime(out var runner, out _);
        runner.EnqueueBaselineSuccess();
        runner.EnqueueSuccess(new ProcessRunResult(
            0,
            JsonSerializer.Serialize(new { type = "result", is_error = true, api_error_status = 503, result = "upstream" }),
            string.Empty));

        var result = await runtime.ValidateCredentialAsync("sk-ant-api03-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.NetworkError);
    }

    [Fact]
    public async Task ValidateCredentialAsync_EmptyCredential_ReturnsInvalidWithoutSpawn()
    {
        var runtime = CreateRuntime(out var runner, out _);

        var result = await runtime.ValidateCredentialAsync("   ", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Invalid);
        runner.InvocationCount.ShouldBe(0);
    }

    // --- REST fallback path (API keys, CLI absent) ---

    [Fact]
    public async Task ValidateCredentialAsync_ApiKey_CliMissing_FallsBackToRestSuccess()
    {
        var handler = new StubHttpHandler();
        handler.Add("api.anthropic.com", HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new[] { new { id = "claude-opus-5" } },
        }));

        var runtime = CreateRuntime(out var runner, out _, handler);
        runner.EnqueueBaselineMissing();

        var result = await runtime.ValidateCredentialAsync("sk-ant-api03-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Valid);
        handler.LastRequest!.Headers.GetValues("x-api-key").ShouldContain("sk-ant-api03-key");
        handler.LastRequest!.Headers.GetValues("anthropic-version").ShouldContain("2023-06-01");
    }

    [Fact]
    public async Task ValidateCredentialAsync_ApiKey_CliMissing_RestUnauthorized_MapsToInvalid()
    {
        var handler = new StubHttpHandler();
        handler.Add("api.anthropic.com", HttpStatusCode.Unauthorized, "{}");

        var runtime = CreateRuntime(out var runner, out _, handler);
        runner.EnqueueBaselineMissing();

        var result = await runtime.ValidateCredentialAsync("sk-ant-api03-bad", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Invalid);
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ValidateCredentialAsync_ApiKey_CliMissing_RestServiceUnavailable_MapsToNetworkError()
    {
        var handler = new StubHttpHandler();
        handler.Add("api.anthropic.com", HttpStatusCode.ServiceUnavailable, "{}");

        var runtime = CreateRuntime(out var runner, out _, handler);
        runner.EnqueueBaselineMissing();

        var result = await runtime.ValidateCredentialAsync("sk-ant-api03-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.NetworkError);
    }

    [Fact]
    public async Task ValidateCredentialAsync_OAuthToken_CliMissing_ReturnsInvalidWithoutRestCall()
    {
        var handler = new StubHttpHandler();
        var runtime = CreateRuntime(out var runner, out _, handler);
        runner.EnqueueBaselineMissing();

        var result = await runtime.ValidateCredentialAsync("sk-ant-oat01-tok", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CredentialValidationStatus.Invalid);
        // #931: the message must stand on its own (no method names, no
        // "confirm X passes" jargon). It mentions the missing CLI and a
        // remediation the operator can act on.
        var message = result.ErrorMessage.ShouldNotBeNull();
        message.ShouldContain("claude");
        message.ShouldContain("CLI");
        message.ShouldNotContain("VerifyContainerBaselineAsync");
        message.ShouldContain("Anthropic API key");
        // Structured code lets the wizard substitute image-aware copy.
        result.Code.ShouldBe(CredentialValidationCodes.BaselineUnavailable);
        handler.CallCount.ShouldBe(0);
    }

    // --- VerifyContainerBaselineAsync ---

    [Fact]
    public async Task VerifyContainerBaselineAsync_CliPresent_ReturnsPassedNoErrors()
    {
        var runtime = CreateRuntime(out var runner, out _);
        runner.EnqueueBaselineSuccess();

        var result = await runtime.VerifyContainerBaselineAsync(TestContext.Current.CancellationToken);

        result.Passed.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task VerifyContainerBaselineAsync_CliMissing_ReturnsFailedWithError()
    {
        var runtime = CreateRuntime(out var runner, out _);
        runner.EnqueueBaselineMissing();

        var result = await runtime.VerifyContainerBaselineAsync(TestContext.Current.CancellationToken);

        result.Passed.ShouldBeFalse();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].ShouldContain("claude");
    }

    [Fact]
    public async Task VerifyContainerBaselineAsync_CliExitsNonZero_ReturnsFailedWithCodeInError()
    {
        var runtime = CreateRuntime(out var runner, out _);
        runner.EnqueueSuccess(new ProcessRunResult(ExitCode: 7, StandardOutput: string.Empty, StandardError: "boom"));

        var result = await runtime.VerifyContainerBaselineAsync(TestContext.Current.CancellationToken);

        result.Passed.ShouldBeFalse();
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].ShouldContain("7");
    }

    // --- Helpers ---

    private static ClaudeAgentRuntime CreateRuntime(
        out StubProcessRunner runner,
        out IHttpClientFactory httpClientFactory,
        StubHttpHandler? handler = null)
    {
        runner = new StubProcessRunner();
        var actualHandler = handler ?? new StubHttpHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(actualHandler, disposeHandler: false));
        httpClientFactory = factory;
        return new ClaudeAgentRuntime(factory, runner, NullLogger<ClaudeAgentRuntime>.Instance);
    }
}