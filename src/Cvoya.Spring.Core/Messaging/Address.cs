// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents an addressable endpoint in the Spring Voyage platform.
/// The scheme identifies the type of addressable (e.g., "agent", "unit", "connector")
/// and the path identifies the specific instance (e.g., "engineering-team/ada").
/// </summary>
/// <param name="Scheme">The address scheme identifying the type of addressable.</param>
/// <param name="Path">The path identifying the specific addressable instance.</param>
public record Address(string Scheme, string Path);
