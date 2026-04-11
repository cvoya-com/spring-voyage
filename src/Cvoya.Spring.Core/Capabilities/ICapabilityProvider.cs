// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

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