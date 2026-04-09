/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Represents a specific domain of expertise that a component possesses.
/// </summary>
/// <param name="Name">The name of the expertise domain.</param>
/// <param name="Description">A description of the expertise domain.</param>
public record ExpertiseDomain(string Name, string Description);
