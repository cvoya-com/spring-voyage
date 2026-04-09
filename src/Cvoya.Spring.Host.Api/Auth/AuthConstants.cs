/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Host.Api.Auth;

/// <summary>
/// Constants for authentication scheme names and default identities used in local dev mode.
/// </summary>
public static class AuthConstants
{
    /// <summary>The authentication scheme name for API token bearer authentication.</summary>
    public const string ApiTokenScheme = "ApiToken";

    /// <summary>The authentication scheme name for local development bypass authentication.</summary>
    public const string LocalDevScheme = "LocalDev";

    /// <summary>The default user ID assigned when running in local dev mode.</summary>
    public const string DefaultLocalUserId = "local-dev-user";

    /// <summary>The default tenant ID assigned when running in local dev mode.</summary>
    public const string DefaultLocalTenantId = "local-dev-tenant";
}
