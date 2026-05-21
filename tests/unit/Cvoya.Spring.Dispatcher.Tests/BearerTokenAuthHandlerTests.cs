// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests;

using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Pins <see cref="BearerTokenAuthHandler"/> auth behaviour, focused on the
/// #2582 fix: the orchestration callback route prefix must not run through
/// — or log a failure under — the <c>DispatcherBearer</c> static-token
/// scheme. The orchestration endpoints own their auth via
/// <c>CallbackTokenValidator</c>; a callback JWT is never a
/// <c>DispatcherOptions.Tokens</c> value, so the scheme must abstain
/// (<see cref="AuthenticateResult.None"/>) rather than fail noisily.
/// </summary>
public class BearerTokenAuthHandlerTests
{
    private static async Task<BearerTokenAuthHandler> CreateInitializedHandlerAsync(HttpContext context)
    {
        var optionsMonitor = Substitute.For<IOptionsMonitor<BearerTokenAuthOptions>>();
        optionsMonitor.Get(Arg.Any<string>()).Returns(new BearerTokenAuthOptions());

        var dispatcherOptions = Substitute.For<IOptionsMonitor<DispatcherOptions>>();
        dispatcherOptions.CurrentValue.Returns(new DispatcherOptions());

        var handler = new BearerTokenAuthHandler(
            optionsMonitor,
            dispatcherOptions,
            NullLoggerFactory.Instance,
            UrlEncoder.Default);

        await handler.InitializeAsync(
            new AuthenticationScheme(
                BearerTokenAuthHandler.SchemeName,
                BearerTokenAuthHandler.SchemeName,
                typeof(BearerTokenAuthHandler)),
            context);

        return handler;
    }

    private static HttpContext ContextFor(string path, string? authHeader)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (authHeader is not null)
        {
            context.Request.Headers.Authorization = authHeader;
        }
        return context;
    }

    [Fact]
    public async Task OrchestrationCallbackPath_WithUnknownBearer_AbstainsInsteadOfFailing()
    {
        // The orchestration callback path carries a callback JWT, never a
        // DispatcherOptions.Tokens value. The static-token scheme must
        // abstain — NoResult, no Failure — so it logs no spurious
        // "DispatcherBearer was not authenticated" line.
        var handler = await CreateInitializedHandlerAsync(
            ContextFor("/v1/runtime/orchestration", "Bearer some-callback-jwt"));

        var result = await handler.AuthenticateAsync();

        result.None.ShouldBeTrue();
        result.Failure.ShouldBeNull();
    }

    [Fact]
    public async Task OrchestrationCallbackSubRoute_WithUnknownBearer_Abstains()
    {
        var handler = await CreateInitializedHandlerAsync(
            ContextFor("/v1/runtime/orchestration/delegate-to", "Bearer some-callback-jwt"));

        var result = await handler.AuthenticateAsync();

        result.None.ShouldBeTrue();
        result.Failure.ShouldBeNull();
    }

    [Fact]
    public async Task NonOrchestrationPath_WithUnknownBearer_StillFails()
    {
        // Every other route keeps the original behaviour: an unrecognised
        // bearer token is a genuine auth failure.
        var handler = await CreateInitializedHandlerAsync(
            ContextFor("/v1/containers", "Bearer not-a-known-token"));

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task NonOrchestrationPath_WithNoAuthHeader_AbstainsAsBefore()
    {
        var handler = await CreateInitializedHandlerAsync(
            ContextFor("/v1/containers", authHeader: null));

        var result = await handler.AuthenticateAsync();

        result.None.ShouldBeTrue();
    }
}
