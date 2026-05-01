// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Security.Claims;
using System.Security.Cryptography;

using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Maps authentication and token management API endpoints.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>
    /// Registers auth endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // C1.2 authz audit: token self-management is in-product usage —
        // every route requires TenantUser (a caller who can use the product
        // should be able to manage their own tokens).
        var group = app.MapGroup("/api/v1/tenant/auth")
            .WithTags("Auth")
            .RequireAuthorization(RolePolicies.TenantUser);

        group.MapPost("/tokens", CreateTokenAsync)
            .WithName("CreateToken")
            .WithSummary("Create a new API token")
            .Produces<CreateTokenResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/tokens", ListTokensAsync)
            .WithName("ListTokens")
            .WithSummary("List all API tokens for the current user")
            .Produces<TokenResponse[]>(StatusCodes.Status200OK);

        group.MapDelete("/tokens/{name}", RevokeTokenAsync)
            .WithName("RevokeToken")
            .WithSummary("Revoke an API token by name")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/me", GetCurrentUserAsync)
            .WithName("GetCurrentUser")
            .WithSummary("Get the current authenticated user's profile")
            .Produces<UserProfileResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return group;
    }

    private static async Task<IResult> GetCurrentUserAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var userIdClaim = user.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userIdClaim))
        {
            return Results.Unauthorized();
        }

        var displayName = user.FindFirstValue(ClaimTypes.Name) ?? userIdClaim;

        // Expose the human:// address so the portal can identify "self" in
        // participant lists without relying on display-name matching (#1485).
        var address = $"human://{userIdClaim}";

        return Results.Ok(new UserProfileResponse(userIdClaim, displayName, address));
    }

    private static async Task<IResult> CreateTokenAsync(
        CreateTokenRequest request,
        ClaimsPrincipal user,
        SpringDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? AuthConstants.DefaultLocalUserId;

        // Check for duplicate name within the same user
        var existingToken = await dbContext.ApiTokens
            .FirstOrDefaultAsync(t => t.Name == request.Name && t.UserId == userId && t.RevokedAt == null, cancellationToken);

        if (existingToken is not null)
        {
            return Results.Problem(detail: $"An active token named '{request.Name}' already exists.", statusCode: StatusCodes.Status409Conflict);
        }

        // Generate a cryptographically random token
        var rawTokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Convert.ToBase64String(rawTokenBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var tokenHash = ApiTokenAuthHandler.HashToken(rawToken);

        var scopes = request.Scopes is not null ? string.Join(",", request.Scopes) : null;

        var entity = new ApiTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = tokenHash,
            Name = request.Name,
            Scopes = scopes,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow
        };

        dbContext.ApiTokens.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/api/v1/tenant/auth/tokens",
            new CreateTokenResponse(rawToken, request.Name));
    }

    private static async Task<IResult> ListTokensAsync(
        ClaimsPrincipal user,
        SpringDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? AuthConstants.DefaultLocalUserId;

        var tokens = await dbContext.ApiTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TokenResponse(
                t.Name,
                t.CreatedAt,
                t.ExpiresAt,
                t.Scopes != null
                    ? t.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                    : null))
            .ToListAsync(cancellationToken);

        return Results.Ok(tokens);
    }

    private static async Task<IResult> RevokeTokenAsync(
        string name,
        ClaimsPrincipal user,
        SpringDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? AuthConstants.DefaultLocalUserId;

        var token = await dbContext.ApiTokens
            .FirstOrDefaultAsync(t => t.Name == name && t.UserId == userId && t.RevokedAt == null, cancellationToken);

        if (token is null)
        {
            return Results.Problem(detail: $"Token '{name}' not found.", statusCode: StatusCodes.Status404NotFound);
        }

        token.RevokedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }
}