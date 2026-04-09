/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Represents a component that advertises its capabilities.
/// </summary>
public interface ICapabilityProvider
{
    /// <summary>
    /// Gets the list of capability identifiers supported by this component.
    /// </summary>
    IReadOnlyList<string> Capabilities { get; }
}
