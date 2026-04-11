// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents a component that has a unique address in the Spring Voyage platform.
/// </summary>
public interface IAddressable
{
    /// <summary>
    /// Gets the address of this component.
    /// </summary>
    Address Address { get; }
}