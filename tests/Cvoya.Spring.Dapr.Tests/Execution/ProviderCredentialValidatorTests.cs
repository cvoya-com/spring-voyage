// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ProviderCredentialValidator"/> (#655). Each
/// provider is exercised on the happy path, the auth-failure path, and a
/// network-error path. The Google branch also checks the <c>models/</c>
/// prefix stripping.
/// </summary>
public class ProviderCredentialValidatorTests
{
    private static readonly IOptions<AiProviderOptions> AnthropicOptions = Options.Create(
        new AiProviderOptions
        {
            BaseUrl = "https://api.anthropic.example",
        });

    [Fact]
    public async Task ValidateAsync_Anthropic_HappyPath_ReturnsModels()
    {
        var handler = new StubHandler();
        handler.Add("api.anthropic.example", HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { id = "claude-opus-5-20260101" },
                new { id = "claude-sonnet-5-20260101" },
            },
        }));

        var validator = CreateValidator(handler);
        var result = await validator.ValidateAsync("anthropic", "sk-ant-test", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.Valid);
        result.Models.ShouldBe(new[] { "claude-opus-5-20260101", "claude-sonnet-5-20260101" });
        result.ErrorMessage.ShouldBeNull();

        handler.LastRequest!.Headers.GetValues("x-api-key").ShouldContain("sk-ant-test");
        handler.LastRequest!.Headers.GetValues("anthropic-version").ShouldContain("2023-06-01");
    }

    [Fact]
    public async Task ValidateAsync_Anthropic_Unauthorized_ReturnsUnauthorized()
    {
        var handler = new StubHandler();
        handler.Add("api.anthropic.example", HttpStatusCode.Unauthorized, "{}");

        var validator = CreateValidator(handler);
        var result = await validator.ValidateAsync("anthropic", "sk-bad", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.Unauthorized);
        result.Models.ShouldBeNull();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ValidateAsync_Anthropic_ServerError_ReturnsProviderError()
    {
        var handler = new StubHandler();
        handler.Add("api.anthropic.example", HttpStatusCode.ServiceUnavailable, "{}");

        var validator = CreateValidator(handler);
        var result = await validator.ValidateAsync("anthropic", "sk-test", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.ProviderError);
        result.Models.ShouldBeNull();
    }

    [Fact]
    public async Task ValidateAsync_OpenAi_HappyPath_FiltersToChatModels()
    {
        var handler = new StubHandler();
        handler.Add("api.openai.com", HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { id = "gpt-4o" },
                new { id = "text-embedding-3-small" },
                new { id = "o3-mini" },
                new { id = "whisper-1" },
            },
        }));

        var validator = CreateValidator(handler);
        var result = await validator.ValidateAsync("openai", "sk-openai", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.Valid);
        result.Models.ShouldBe(new[] { "gpt-4o", "o3-mini" });
        handler.LastRequest!.Headers.GetValues("Authorization").ShouldContain("Bearer sk-openai");
    }

    [Fact]
    public async Task ValidateAsync_OpenAi_Unauthorized()
    {
        var handler = new StubHandler();
        handler.Add("api.openai.com", HttpStatusCode.Forbidden, "{}");

        var validator = CreateValidator(handler);
        var result = await validator.ValidateAsync("openai", "sk-bad", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.Unauthorized);
    }

    [Fact]
    public async Task ValidateAsync_Google_HappyPath_StripsModelsPrefix()
    {
        var handler = new StubHandler();
        handler.Add("generativelanguage.googleapis.com", HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            models = new[]
            {
                new { name = "models/gemini-2.5-pro" },
                new { name = "models/gemini-2.5-flash" },
                new { name = "no-prefix" },
            },
        }));

        var validator = CreateValidator(handler);
        var result = await validator.ValidateAsync("gemini", "goog-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.Valid);
        result.Models.ShouldBe(new[] { "gemini-2.5-pro", "gemini-2.5-flash", "no-prefix" });
    }

    [Fact]
    public async Task ValidateAsync_Google_BadRequest_ReturnsUnauthorized()
    {
        // Google returns 400 (not 401) for invalid API keys, so the
        // validator must fold 400 into the Unauthorized bucket for the
        // wizard UX.
        var handler = new StubHandler();
        handler.Add("generativelanguage.googleapis.com", HttpStatusCode.BadRequest, "{}");

        var validator = CreateValidator(handler);
        var result = await validator.ValidateAsync("google", "bad-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.Unauthorized);
    }

    [Fact]
    public async Task ValidateAsync_NetworkError_ReturnsNetworkError()
    {
        var handler = new ThrowingHandler(new HttpRequestException("dns failure"));
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync("openai", "sk-test", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.NetworkError);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("dns failure");
    }

    [Fact]
    public async Task ValidateAsync_EmptyKey_ReturnsMissingKey_WithoutHttpCall()
    {
        var handler = new StubHandler();
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync("anthropic", "   ", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.MissingKey);
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ValidateAsync_Anthropic_CliAvailable_DelegatesToCli_AndBackfillsModels()
    {
        // #660: when the claude CLI is available we delegate both API
        // keys and OAuth tokens to it. The CLI does not expose a
        // models subcommand, so success must still produce a non-empty
        // model list via the static curated fallback.
        var handler = new StubHandler();
        var cli = new FakeCliInvoker
        {
            Available = true,
            Result = new ProviderCliValidationResult(
                ProviderCredentialValidationStatus.Valid,
                Models: null,
                ErrorMessage: null),
        };
        var validator = CreateValidator(handler, cli);

        var result = await validator.ValidateAsync(
            "anthropic", "sk-ant-oat01-secret-token", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.Valid);
        result.Models.ShouldNotBeNull();
        result.Models.ShouldNotBeEmpty();
        cli.LastCredential.ShouldBe("sk-ant-oat01-secret-token");
        handler.CallCount.ShouldBe(0); // REST path skipped when CLI handled it
    }

    [Fact]
    public async Task ValidateAsync_Anthropic_CliAvailable_PropagatesUnauthorized()
    {
        var handler = new StubHandler();
        var cli = new FakeCliInvoker
        {
            Available = true,
            Result = new ProviderCliValidationResult(
                ProviderCredentialValidationStatus.Unauthorized,
                Models: null,
                ErrorMessage: "Anthropic rejected the credential."),
        };
        var validator = CreateValidator(handler, cli);

        var result = await validator.ValidateAsync(
            "anthropic", "sk-ant-oat01-stale", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.Unauthorized);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("Anthropic rejected");
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ValidateAsync_Anthropic_CliUnavailable_OAuthToken_ReturnsSpecificError()
    {
        // #660 core case: the host has no claude CLI and the operator
        // pastes a Claude.ai OAuth token. The validator must NOT
        // fall through to the REST call (which will surface a generic
        // 401); it must surface the specific error.
        var handler = new StubHandler();
        var cli = new FakeCliInvoker { Available = false };
        var validator = CreateValidator(handler, cli);

        var result = await validator.ValidateAsync(
            "anthropic", "sk-ant-oat01-secret", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.ProviderError);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("claude CLI");
        result.ErrorMessage.ShouldContain("Claude.ai");
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ValidateAsync_Anthropic_CliUnavailable_ApiKey_FallsBackToRest()
    {
        // Non-OAuth keys (the traditional API key) still work through
        // the Anthropic Platform REST path, so the validator must
        // fall through to the existing HTTP flow when the CLI is
        // unavailable rather than block valid API keys.
        var handler = new StubHandler();
        handler.Add("api.anthropic.example", HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { id = "claude-sonnet-5-20260101" },
            },
        }));
        var cli = new FakeCliInvoker { Available = false };
        var validator = CreateValidator(handler, cli);

        var result = await validator.ValidateAsync(
            "anthropic", "sk-ant-api03-live-key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.Valid);
        result.Models.ShouldBe(new[] { "claude-sonnet-5-20260101" });
        handler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ValidateAsync_Anthropic_NoCliInvokerRegistered_OAuthToken_ReturnsSpecificError()
    {
        // Same error path when no CLI invoker is registered at all
        // (belt-and-suspenders for deployments that disable the
        // default registration).
        var handler = new StubHandler();
        var validator = CreateValidator(handler, cliInvoker: null);

        var result = await validator.ValidateAsync(
            "anthropic", "sk-ant-oat01-no-cli", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.ProviderError);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("claude CLI");
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ValidateAsync_UnknownProvider_ReturnsUnknownProvider()
    {
        var handler = new StubHandler();
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync("no-such-provider", "key", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(ProviderCredentialValidationStatus.UnknownProvider);
        handler.CallCount.ShouldBe(0);
    }

    private static ProviderCredentialValidator CreateValidator(
        HttpMessageHandler handler,
        IProviderCliInvoker? cliInvoker = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        var invokers = cliInvoker is null
            ? Array.Empty<IProviderCliInvoker>()
            : new[] { cliInvoker };

        return new ProviderCredentialValidator(
            factory,
            AnthropicOptions,
            invokers,
            NullLogger<ProviderCredentialValidator>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode, string)> _responses = new();

        public HttpRequestMessage? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        public void Add(string host, HttpStatusCode status, string body) =>
            _responses[host] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

            var host = request.RequestUri?.Host ?? string.Empty;
            if (!_responses.TryGetValue(host, out var r))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent($"no stub for {host}"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(r.Item1)
            {
                Content = new StringContent(r.Item2, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw exception;
        }
    }

    /// <summary>Test double that pretends to be the claude CLI invoker.</summary>
    private sealed class FakeCliInvoker : IProviderCliInvoker
    {
        public string ProviderId => "anthropic";
        public bool Available { get; set; }
        public ProviderCliValidationResult Result { get; set; } =
            new(ProviderCredentialValidationStatus.Valid, Models: null, ErrorMessage: null);
        public string? LastCredential { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Available);

        public Task<ProviderCliValidationResult> ValidateAsync(string credential, CancellationToken cancellationToken = default)
        {
            LastCredential = credential;
            return Task.FromResult(Result);
        }
    }
}