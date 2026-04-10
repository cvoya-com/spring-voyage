/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Host.Api.Auth;

/// <summary>
/// Configuration options for GitHub OAuth authentication.
/// Bound from the "OAuth:GitHub" configuration section.
/// </summary>
public class OAuthOptions
{
    /// <summary>The configuration section name for these options.</summary>
    public const string SectionName = "OAuth:GitHub";

    /// <summary>Gets or sets the GitHub OAuth App client ID.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Gets or sets the GitHub OAuth App client secret.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Gets or sets the callback URL that GitHub redirects to after authorization.</summary>
    public string CallbackUrl { get; set; } = string.Empty;
}
