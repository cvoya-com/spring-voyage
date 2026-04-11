// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Cloning;

/// <summary>
/// Defines the memory policy for agent cloning.
/// </summary>
public enum CloningPolicy
{
    /// <summary>
    /// No cloning allowed.
    /// </summary>
    None,

    /// <summary>
    /// Ephemeral clone without memory — starts with a blank state.
    /// </summary>
    EphemeralNoMemory,

    /// <summary>
    /// Ephemeral clone with memory — copies the parent's memory state at creation time.
    /// </summary>
    EphemeralWithMemory
}