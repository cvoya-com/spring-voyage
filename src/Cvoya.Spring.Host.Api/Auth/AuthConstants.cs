// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

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
}