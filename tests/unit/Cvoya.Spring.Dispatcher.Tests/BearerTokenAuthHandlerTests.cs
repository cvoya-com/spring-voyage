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
/// Pins <see cref="BearerTokenAuthHandler"/> auth behaviour: an unrecognised
/// bearer token is a genuine failure, a missing header abstains. The
/// runtime callback surface relocated off the dispatcher onto the
/// Dapr-connected API host (#2586), so the dispatcher no longer carries a
/// callback-path special case.
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
    public async Task UnknownBearer_Fails()
    {
        // An unrecognised bearer token is a genuine auth failure.
        var handler = await CreateInitializedHandlerAsync(
            ContextFor("/v1/containers", "Bearer not-a-known-token"));

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task NoAuthHeader_Abstains()
    {
        var handler = await CreateInitializedHandlerAsync(
            ContextFor("/v1/containers", authHeader: null));

        var result = await handler.AuthenticateAsync();

        result.None.ShouldBeTrue();
    }
}
