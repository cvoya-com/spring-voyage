// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Represents a component's expertise profile, including a summary and specific domains.
/// </summary>
/// <param name="Summary">A brief summary of the component's overall expertise.</param>
/// <param name="Domains">The specific domains of expertise.</param>
public record ExpertiseProfile(string Summary, IReadOnlyList<ExpertiseDomain> Domains);
