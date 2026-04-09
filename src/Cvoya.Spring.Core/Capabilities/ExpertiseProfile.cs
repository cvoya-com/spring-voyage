/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Represents a component's expertise profile, including a summary and specific domains.
/// </summary>
/// <param name="Summary">A brief summary of the component's overall expertise.</param>
/// <param name="Domains">The specific domains of expertise.</param>
public record ExpertiseProfile(string Summary, IReadOnlyList<ExpertiseDomain> Domains);
