// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Maps authentication and token management API endpoints.
/// </summary>
public static class AuthEndpoints
{
    private const string OAuthStateCookieName = "spring_oauth_state";

    private static readonly EventId LoginRedirectEventId = new(3010, "LoginRedirect");
    private static readonly EventId OAuthCallbackEventId = new(3011, "OAuthCallback");
    private static readonly EventId OAuthCallbackFailedEventId = new(3012, "OAuthCallbackFailed");
    private static readonly EventId UserCreatedEventId = new(3013, "UserCreated");
    private static readonly EventId UserLogoutEventId = new(3014, "UserLogout");

    /// <summary>
    /// Registers auth endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth")
            .WithTags("Auth");

        group.MapPost("/tokens", CreateTokenAsync)
            .WithName("CreateToken")
            .WithSummary("Create a new API token")
            .RequireAuthorization();

        group.MapGet("/tokens", ListTokensAsync)
            .WithName("ListTokens")
            .WithSummary("List all API tokens for the current user")
            .RequireAuthorization();

        group.MapDelete("/tokens/{name}", RevokeTokenAsync)
            .WithName("RevokeToken")
            .WithSummary("Revoke an API token by name")
            .RequireAuthorization();

        group.MapGet("/login", LoginAsync)
            .WithName("Login")
            .WithSummary("Redirect to GitHub OAuth authorization")
            .AllowAnonymous();

        group.MapGet("/callback", CallbackAsync)
            .WithName("OAuthCallback")
            .WithSummary("Handle GitHub OAuth callback")
            .AllowAnonymous();

        group.MapPost("/logout", Logout)
            .WithName("Logout")
            .WithSummary("Clear session cookie and log out")
            .AllowAnonymous();

        group.MapGet("/me", GetCurrentUserAsync)
            .WithName("GetCurrentUser")
            .WithSummary("Get the current authenticated user's profile")
            .RequireAuthorization();

        return group;
    }

    private static IResult LoginAsync(
        HttpContext httpContext,
        IOptions<OAuthOptions> oauthOptions,
        ILogger<Program> logger)
    {
        var options = oauthOptions.Value;

        if (string.IsNullOrEmpty(options.ClientId))
        {
            return Results.Problem(
                detail: "OAuth is not configured. Use API tokens or --local mode.",
                statusCode: 501);
        }

        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        httpContext.Response.Cookies.Append(OAuthStateCookieName, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/api/v1/auth"
        });

        var redirectUrl = $"https://github.com/login/oauth/authorize" +
            $"?client_id={Uri.EscapeDataString(options.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(options.CallbackUrl)}" +
            $"&scope={Uri.EscapeDataString("read:user user:email")}" +
            $"&state={Uri.EscapeDataString(state)}";

        logger.Log(LogLevel.Information, LoginRedirectEventId, "Redirecting user to GitHub OAuth");

        return Results.Redirect(redirectUrl);
    }

    private static async Task<IResult> CallbackAsync(
        string? code,
        string? state,
        HttpContext httpContext,
        IOptions<OAuthOptions> oauthOptions,
        IHttpClientFactory httpClientFactory,
        SpringDbContext dbContext,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        var options = oauthOptions.Value;

        // Validate CSRF state parameter
        if (string.IsNullOrEmpty(state)
            || !httpContext.Request.Cookies.TryGetValue(OAuthStateCookieName, out var expectedState)
            || state != expectedState)
        {
            logger.Log(LogLevel.Warning, OAuthCallbackFailedEventId, "OAuth callback failed: invalid state parameter");
            return Results.BadRequest(new { Error = "Invalid or missing OAuth state parameter." });
        }

        // Clear the state cookie
        httpContext.Response.Cookies.Delete(OAuthStateCookieName, new CookieOptions
        {
            Path = "/api/v1/auth"
        });

        if (string.IsNullOrEmpty(code))
        {
            logger.Log(LogLevel.Warning, OAuthCallbackFailedEventId, "OAuth callback failed: missing authorization code");
            return Results.BadRequest(new { Error = "Missing authorization code." });
        }

        // Exchange code for access token
        var httpClient = httpClientFactory.CreateClient("GitHub");

        var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = options.CallbackUrl
        });

        var tokenHttpRequest = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = tokenRequest
        };
        tokenHttpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var tokenResponse = await httpClient.SendAsync(tokenHttpRequest, cancellationToken);

        if (!tokenResponse.IsSuccessStatusCode)
        {
            logger.Log(LogLevel.Warning, OAuthCallbackFailedEventId, "OAuth callback failed: token exchange returned {StatusCode}", tokenResponse.StatusCode);
            return Results.Problem(
                detail: "Failed to exchange authorization code for access token.",
                statusCode: 502);
        }

        var tokenResult = await tokenResponse.Content.ReadFromJsonAsync<GitHubTokenResponse>(cancellationToken);

        if (tokenResult is null || string.IsNullOrEmpty(tokenResult.AccessToken))
        {
            logger.Log(LogLevel.Warning, OAuthCallbackFailedEventId, "OAuth callback failed: empty access token");
            return Results.Problem(
                detail: "GitHub returned an empty access token.",
                statusCode: 502);
        }

        // Fetch user profile from GitHub
        var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.AccessToken);
        userRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("SpringVoyage", "2.0"));

        var userResponse = await httpClient.SendAsync(userRequest, cancellationToken);

        if (!userResponse.IsSuccessStatusCode)
        {
            logger.Log(LogLevel.Warning, OAuthCallbackFailedEventId, "OAuth callback failed: GitHub user API returned {StatusCode}", userResponse.StatusCode);
            return Results.Problem(
                detail: "Failed to fetch user profile from GitHub.",
                statusCode: 502);
        }

        var githubUser = await userResponse.Content.ReadFromJsonAsync<GitHubUserResponse>(cancellationToken);

        if (githubUser is null)
        {
            return Results.Problem(
                detail: "Failed to parse GitHub user profile.",
                statusCode: 502);
        }

        // Create or update user in database
        var gitHubId = githubUser.Id.ToString();
        var user = await dbContext.Users
            .FirstOrDefaultAsync(u => u.GitHubId == gitHubId, cancellationToken);

        if (user is null)
        {
            user = new UserEntity
            {
                Id = Guid.NewGuid(),
                GitHubId = gitHubId,
                GitHubLogin = githubUser.Login,
                DisplayName = githubUser.Name ?? githubUser.Login,
                Email = githubUser.Email,
                AvatarUrl = githubUser.AvatarUrl
            };

            dbContext.Users.Add(user);
            logger.Log(LogLevel.Information, UserCreatedEventId, "Created new user {GitHubLogin}", githubUser.Login);
        }
        else
        {
            user.GitHubLogin = githubUser.Login;
            user.DisplayName = githubUser.Name ?? githubUser.Login;
            user.Email = githubUser.Email;
            user.AvatarUrl = githubUser.AvatarUrl;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Set session cookie
        httpContext.Response.Cookies.Append(OAuthAuthHandler.SessionCookieName, user.Id.ToString(), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(7),
            Path = "/"
        });

        logger.Log(LogLevel.Information, OAuthCallbackEventId, "OAuth callback succeeded for user {GitHubLogin}", githubUser.Login);

        return Results.Redirect("/");
    }

    private static IResult Logout(
        HttpContext httpContext,
        ILogger<Program> logger)
    {
        httpContext.Response.Cookies.Delete(OAuthAuthHandler.SessionCookieName, new CookieOptions
        {
            Path = "/"
        });

        logger.Log(LogLevel.Information, UserLogoutEventId, "User logged out");

        return Results.Ok(new { Message = "Logged out successfully." });
    }

    private static async Task<IResult> GetCurrentUserAsync(
        ClaimsPrincipal user,
        SpringDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var userIdClaim = user.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userIdClaim))
        {
            return Results.Unauthorized();
        }

        // For local dev mode, return a placeholder profile
        if (userIdClaim == AuthConstants.DefaultLocalUserId)
        {
            return Results.Ok(new UserProfileResponse(
                Guid.Empty,
                "local-dev",
                "Local Developer",
                null,
                null));
        }

        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Unauthorized();
        }

        var userEntity = await dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (userEntity is null)
        {
            return Results.NotFound(new { Error = "User not found." });
        }

        return Results.Ok(new UserProfileResponse(
            userEntity.Id,
            userEntity.GitHubLogin,
            userEntity.DisplayName,
            userEntity.Email,
            userEntity.AvatarUrl));
    }

    private static async Task<IResult> CreateTokenAsync(
        CreateTokenRequest request,
        ClaimsPrincipal user,
        SpringDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? AuthConstants.DefaultLocalUserId;
        var tenantIdClaim = user.FindFirstValue("tenant_id") ?? AuthConstants.DefaultLocalTenantId;

        if (!Guid.TryParse(tenantIdClaim, out var tenantId))
        {
            tenantId = Guid.Empty;
        }

        // Check for duplicate name within the same user
        var existingToken = await dbContext.ApiTokens
            .FirstOrDefaultAsync(t => t.Name == request.Name && t.UserId == userId && t.RevokedAt == null, cancellationToken);

        if (existingToken is not null)
        {
            return Results.Conflict(new { Error = $"An active token named '{request.Name}' already exists." });
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
            TenantId = tenantId,
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
            $"/api/v1/auth/tokens",
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
            return Results.NotFound(new { Error = $"Token '{name}' not found." });
        }

        token.RevokedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }
}
