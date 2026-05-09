// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

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
