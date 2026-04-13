// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Cloning;

using System.Text.Json.Serialization;

/// <summary>
/// Defines the memory policy for agent cloning. Serialised to JSON using
/// the kebab-case wire values below (preserved via
/// <see cref="JsonStringEnumMemberNameAttribute"/>) so existing clients
/// keep working — see #183 for the motivation.
/// </summary>
public enum CloningPolicy
{
    /// <summary>
    /// No cloning allowed.
    /// </summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>
    /// Ephemeral clone without memory — starts with a blank state.
    /// </summary>
    [JsonStringEnumMemberName("ephemeral-no-memory")]
    EphemeralNoMemory,

    /// <summary>
    /// Ephemeral clone with memory — copies the parent's memory state at creation time.
    /// </summary>
    [JsonStringEnumMemberName("ephemeral-with-memory")]
    EphemeralWithMemory
}