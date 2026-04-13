// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Maps endpoints under <c>/api/v1/integrations</c> — external-system helpers
/// the wizard and the unit config page need to offer a usable connector-setup
/// flow. Currently only GitHub is wired; other integrations will land as
/// sibling groups on this route.
/// </summary>
public static class IntegrationsEndpoints
{
    /// <summary>
    /// Registers integration endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapIntegrationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integrations")
            .WithTags("Integrations");

        group.MapGet("/github/installations", ListGitHubInstallationsAsync)
            .WithName("ListGitHubInstallations")
            .WithSummary("List GitHub App installations visible to the configured App")
            .Produces<GitHubInstallationResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapGet("/github/install-url", GetGitHubInstallUrlAsync)
            .WithName("GetGitHubInstallUrl")
            .WithSummary("Get the GitHub App install URL the wizard should redirect the user through")
            .Produces<GitHubInstallUrlResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return group;
    }

    private static async Task<IResult> ListGitHubInstallationsAsync(
        [FromServices] IGitHubInstallationsClient client,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.IntegrationsEndpoints");

        try
        {
            var installations = await client.ListInstallationsAsync(cancellationToken);

            var response = installations
                .Select(i => new GitHubInstallationResponse(
                    i.InstallationId, i.Account, i.AccountType, i.RepoSelection))
                .ToArray();

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            // Wrap Octokit / App-auth failures (missing PEM, bad JWT, 5xx from
            // GitHub, etc.) as 502 so the wizard can show a useful error
            // without a stack trace. Keeping the message in `detail` mirrors
            // the existing endpoint convention.
            logger.LogWarning(ex, "Failed to list GitHub App installations");
            return Results.Problem(
                title: "Failed to list GitHub App installations",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult GetGitHubInstallUrlAsync(
        [FromServices] IOptions<GitHubConnectorOptions> options)
    {
        var slug = options.Value.AppSlug;
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Results.Problem(
                title: "GitHub App slug is not configured",
                detail: "Configure 'GitHub:AppSlug' so the platform can build the install URL.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var url = $"https://github.com/apps/{Uri.EscapeDataString(slug)}/installations/new";
        return Results.Ok(new GitHubInstallUrlResponse(url));
    }
}