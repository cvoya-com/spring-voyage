// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Represents a specific domain of expertise that a component possesses.
/// </summary>
/// <param name="Name">The name of the expertise domain.</param>
/// <param name="Description">A description of the expertise domain.</param>
public record ExpertiseDomain(string Name, string Description);
