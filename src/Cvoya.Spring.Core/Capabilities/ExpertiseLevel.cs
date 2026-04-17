// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Describes how deep a component's expertise on a given domain goes. Mirrors
/// the <c>level</c> field in agent YAML (<c>beginner | intermediate | advanced
/// | expert</c>) so the platform can preserve round-trip fidelity when
/// importing and exporting definitions.
/// </summary>
/// <remarks>
/// Ordered from weakest to strongest so callers can apply comparisons
/// (e.g. "advanced or above") without an auxiliary map.
/// </remarks>
public enum ExpertiseLevel
{
    /// <summary>Novice / beginner familiarity.</summary>
    Beginner = 0,

    /// <summary>Working knowledge.</summary>
    Intermediate = 1,

    /// <summary>Strong, independent mastery.</summary>
    Advanced = 2,

    /// <summary>Domain authority.</summary>
    Expert = 3,
}