// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

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
