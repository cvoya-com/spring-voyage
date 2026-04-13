// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <c>GET /api/v1/integrations/github/installations</c>
/// and <c>GET /api/v1/integrations/github/install-url</c>. Uses its own
/// <see cref="WebApplicationFactory{TEntryPoint}"/> so it can reconfigure
/// <see cref="GitHubConnectorOptions"/> per test — the shared
/// <see cref="CustomWebApplicationFactory"/> binds the defaults.
/// </summary>
public class IntegrationsEndpointsTests
{
    [Fact]
    public async Task ListInstallations_HappyPath_ReturnsProjection()
    {
        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallation(1001L, "acme", "Organization", "selected"),
                new GitHubInstallation(1002L, "alice", "User", "all"),
            });

        await using var factory = CreateFactory(installationsClient: installationsClient);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/integrations/github/installations", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GitHubInstallationResponse[]>(ct);
        body.ShouldNotBeNull();
        body!.Length.ShouldBe(2);
        body[0].InstallationId.ShouldBe(1001L);
        body[0].Account.ShouldBe("acme");
        body[0].AccountType.ShouldBe("Organization");
        body[0].RepoSelection.ShouldBe("selected");
        body[1].InstallationId.ShouldBe(1002L);
        body[1].AccountType.ShouldBe("User");
        body[1].RepoSelection.ShouldBe("all");
    }

    [Fact]
    public async Task ListInstallations_OctokitThrows_Returns502()
    {
        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("github 500"));

        await using var factory = CreateFactory(installationsClient: installationsClient);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/integrations/github/installations", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task GetInstallUrl_AppSlugConfigured_ReturnsInstallUrl()
    {
        await using var factory = CreateFactory(appSlug: "spring-voyage-test");
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/integrations/github/install-url", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GitHubInstallUrlResponse>(ct);
        body.ShouldNotBeNull();
        body!.Url.ShouldBe("https://github.com/apps/spring-voyage-test/installations/new");
    }

    [Fact]
    public async Task GetInstallUrl_NoAppSlug_Returns502()
    {
        await using var factory = CreateFactory(appSlug: string.Empty);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/integrations/github/install-url", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    /// <summary>
    /// Wraps <see cref="CustomWebApplicationFactory"/> with per-test overrides
    /// of <see cref="GitHubConnectorOptions"/> and optional
    /// <see cref="IGitHubInstallationsClient"/>. Kept local to avoid
    /// polluting the shared fixture that other endpoint tests rely on.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        string? appSlug = null,
        IGitHubInstallationsClient? installationsClient = null)
    {
        var baseFactory = new CustomWebApplicationFactory();
        return baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                if (appSlug is not null)
                {
                    services.PostConfigure<GitHubConnectorOptions>(opts => opts.AppSlug = appSlug);
                }

                if (installationsClient is not null)
                {
                    // WithWebHostBuilder re-runs ConfigureWebHost, so replace the
                    // base factory's substitute with the test-specific one.
                    var descriptors = services
                        .Where(d => d.ServiceType == typeof(IGitHubInstallationsClient))
                        .ToList();
                    foreach (var d in descriptors)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton(installationsClient);
                }
            });
        });
    }
}