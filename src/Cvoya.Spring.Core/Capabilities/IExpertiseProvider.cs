/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Represents a component that can describe its areas of expertise.
/// </summary>
public interface IExpertiseProvider
{
    /// <summary>
    /// Gets the expertise profile of this component.
    /// </summary>
    /// <returns>The expertise profile.</returns>
    ExpertiseProfile GetExpertise();
}
