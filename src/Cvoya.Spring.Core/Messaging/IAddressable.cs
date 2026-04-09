/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

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
