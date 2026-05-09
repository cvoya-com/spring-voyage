// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

/// <summary>
/// Configuration options for unit runtime container launches.
/// Bound from the <c>UnitRuntime</c> configuration section.
/// </summary>
public class UnitRuntimeOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "UnitRuntime";

    /// <summary>
    /// Gets or sets the container image used when a unit is started.
    /// </summary>
    public string Image { get; set; } = "ghcr.io/cvoya-com/spring-voyage-agent:latest";

    /// <summary>
    /// Gets or sets the port the unit's application container listens on.
    /// </summary>
    public int AppPort { get; set; } = 8080;
}
