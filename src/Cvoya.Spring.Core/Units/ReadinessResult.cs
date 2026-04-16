// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using System.Runtime.Serialization;

/// <summary>
/// Result of a unit readiness check. Describes whether the unit has enough
/// configuration to leave the <see cref="UnitStatus.Draft"/> state and lists
/// the requirements that are not yet satisfied.
/// </summary>
/// <param name="IsReady">True when the unit meets all minimum requirements and can be started.</param>
/// <param name="MissingRequirements">
/// Human-readable labels for each unsatisfied requirement (e.g. <c>"model"</c>).
/// Empty when <paramref name="IsReady"/> is <c>true</c>.
/// </param>
[DataContract]
public record ReadinessResult(
    [property: DataMember(Order = 0)] bool IsReady,
    [property: DataMember(Order = 1)] string[] MissingRequirements);