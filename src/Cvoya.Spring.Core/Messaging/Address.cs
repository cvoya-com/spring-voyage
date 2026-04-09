/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents an addressable endpoint in the Spring Voyage platform.
/// The scheme identifies the type of addressable (e.g., "agent", "unit", "connector")
/// and the path identifies the specific instance (e.g., "engineering-team/ada").
/// </summary>
/// <param name="Scheme">The address scheme identifying the type of addressable.</param>
/// <param name="Path">The path identifying the specific addressable instance.</param>
public record Address(string Scheme, string Path);
