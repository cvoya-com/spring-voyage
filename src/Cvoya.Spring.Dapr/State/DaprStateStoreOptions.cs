/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Dapr.State;

/// <summary>
/// Configuration options for the Dapr state store component.
/// </summary>
public class DaprStateStoreOptions
{
    /// <summary>
    /// The configuration section name used for binding.
    /// </summary>
    public const string SectionName = "DaprStateStore";

    /// <summary>
    /// Gets or sets the Dapr state store component name.
    /// </summary>
    public string StoreName { get; set; } = "statestore";
}
