// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.ComponentModel;
using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ClaudeCliInvoker"/> (#660). Each test
/// drives the invoker through an <see cref="IProcessRunner"/> stub so
/// the real <c>claude</c> executable is never invoked.
/// </summary>
public class ClaudeCliInvokerTests
{
    [Fact]
    public async Task IsAvailableAsync_CliPresent_ReturnsTrue()
    {
        var runner = new StubProcessRunner();
        runner.EnqueueSuccess(new ProcessRunResult(0, "1.0.0", string.Empty));

        var invoker = new ClaudeCliInvoker(runner, NullLogger<ClaudeCliInvoker>.Instance);
        var available = await invoker.IsAvailableAsync(TestContext.Current.CancellationToken);

        available.ShouldBeTrue();
    }

    [Fact]
    public async Task IsAvailableAsync_CliMissing_ReturnsFalse()
    {
        var runner = new StubProcessRunner { ThrowWin32OnNext = true };
        var invoker = new ClaudeCliInvoker(runner, NullLogger<ClaudeCliInvoker>.Instance);

        var available = await invoker.IsAvailableAsync(TestContext.Current.CancellationToken);

        available.ShouldBeFalse();
    }

    [Fact]
    public async Task IsAvailableAsync_Caches_OnlyOneProbe()
    {
        var runner = new StubProcessRunner();
        runner.EnqueueSuccess(new ProcessRunResult(0, "1.0.0", string.Empty));
        var invoker = new ClaudeCliInvoker(runner, NullLogger<ClaudeCliInvoker>.Instance);

        await invoker.IsAvailableAsync(TestContext.Current.CancellationToken);
        await invoker.IsAvailableAsync(TestContext.Current.CancellationToken);

        runner.InvocationCount.ShouldBe(1);
    }

    [Fact]
    public async Task ValidateAsync_EmptyCredential_ReturnsMissingKey_WithoutSpawn()
    {
        var runner = new StubProcessRunner();
        var invoker = new ClaudeCliInvoker(runner, NullLogger<ClaudeCliInvoker>.Instance);

        var result = await invoker.ValidateAsync("   ", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.MissingKey);
        runner.InvocationCount.ShouldBe(0);
    }

    [Fact]
    public async Task ValidateAsync_OAuthToken_PlumbsThroughOAuthEnvVar()
    {
        var runner = new StubProcessRunner();
        runner.EnqueueSuccess(new ProcessRunResult(
            0,
            JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result = "OK" }),
            string.Empty));
        var invoker = new ClaudeCliInvoker(runner, NullLogger<ClaudeCliInvoker>.Instance);

        var result = await invoker.ValidateAsync("sk-ant-oat01-tok", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.Valid);
        var env = runner.LastEnvironment!;
        env.ShouldContainKeyAndValue("CLAUDE_CODE_OAUTH_TOKEN", "sk-ant-oat01-tok");
        env.ShouldNotContainKey("ANTHROPIC_API_KEY");
    }

    [Fact]
    public async Task ValidateAsync_ApiKey_PlumbsThroughApiKeyEnvVar()
    {
        var runner = new StubProcessRunner();
        runner.EnqueueSuccess(new ProcessRunResult(
            0,
            JsonSerializer.Serialize(new { type = "result", is_error = false, result = "OK" }),
            string.Empty));
        var invoker = new ClaudeCliInvoker(runner, NullLogger<ClaudeCliInvoker>.Instance);

        await invoker.ValidateAsync("sk-ant-api03-key", TestContext.Current.CancellationToken);

        var env = runner.LastEnvironment!;
        env.ShouldContainKeyAndValue("ANTHROPIC_API_KEY", "sk-ant-api03-key");
        env.ShouldNotContainKey("CLAUDE_CODE_OAUTH_TOKEN");
    }

    [Fact]
    public async Task ValidateAsync_CliReports401_MapsToUnauthorized()
    {
        var runner = new StubProcessRunner();
        runner.EnqueueSuccess(new ProcessRunResult(
            0,
            JsonSerializer.Serialize(new
            {
                type = "result",
                is_error = true,
                api_error_status = 401,
                result = "Invalid API key",
            }),
            string.Empty));
        var invoker = new ClaudeCliInvoker(runner, NullLogger<ClaudeCliInvoker>.Instance);

        var result = await invoker.ValidateAsync("sk-ant-api03-bad", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.Unauthorized);
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ValidateAsync_CliReports5xx_MapsToProviderError()
    {
        var runner = new StubProcessRunner();
        runner.EnqueueSuccess(new ProcessRunResult(
            0,
            JsonSerializer.Serialize(new
            {
                type = "result",
                is_error = true,
                api_error_status = 503,
                result = "upstream unavailable",
            }),
            string.Empty));
        var invoker = new ClaudeCliInvoker(runner, NullLogger<ClaudeCliInvoker>.Instance);

        var result = await invoker.ValidateAsync("sk-ant-api03-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.ProviderError);
    }

    [Fact]
    public async Task ValidateAsync_CliNotFoundAtSpawnTime_MapsToProviderError()
    {
        var runner = new StubProcessRunner { ThrowWin32OnNext = true };
        var invoker = new ClaudeCliInvoker(runner, NullLogger<ClaudeCliInvoker>.Instance);

        var result = await invoker.ValidateAsync("sk-ant-api03-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.ProviderError);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("claude CLI");
    }

    [Fact]
    public async Task ValidateAsync_UnparseableStdout_MapsToProviderError()
    {
        var runner = new StubProcessRunner();
        runner.EnqueueSuccess(new ProcessRunResult(0, "not json", string.Empty));
        var invoker = new ClaudeCliInvoker(runner, NullLogger<ClaudeCliInvoker>.Instance);

        var result = await invoker.ValidateAsync("sk-ant-api03-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.ProviderError);
    }

    /// <summary>Queue-backed <see cref="IProcessRunner"/> for deterministic tests.</summary>
    private sealed class StubProcessRunner : IProcessRunner
    {
        private readonly Queue<ProcessRunResult> _results = new();
        public int InvocationCount { get; private set; }
        public IReadOnlyDictionary<string, string>? LastEnvironment { get; private set; }
        public IReadOnlyList<string>? LastArguments { get; private set; }
        public bool ThrowWin32OnNext { get; set; }
        public bool ThrowTimeoutOnNext { get; set; }

        public void EnqueueSuccess(ProcessRunResult result) => _results.Enqueue(result);

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            LastEnvironment = environment;
            LastArguments = arguments;
            if (ThrowWin32OnNext) throw new Win32Exception("simulated: CLI not found");
            if (ThrowTimeoutOnNext) throw new TimeoutException("simulated: CLI timeout");
            return Task.FromResult(_results.Dequeue());
        }
    }
}