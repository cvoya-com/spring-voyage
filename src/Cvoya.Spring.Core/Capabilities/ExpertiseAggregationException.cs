// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Thrown by <see cref="IExpertiseAggregator"/> implementations when the
/// recursive walk over a unit's member graph cannot complete — typically
/// because the graph contains a cycle (misconfigured parent chain) or
/// exceeds the safety depth cap. The exception carries the offending path
/// so operators can act on a precise diagnostic.
/// </summary>
public class ExpertiseAggregationException : SpringException
{
    /// <summary>
    /// Address of the unit whose aggregation was requested.
    /// </summary>
    public Address Unit { get; }

    /// <summary>
    /// Ordered addresses walked before the walk bailed out.
    /// </summary>
    public IReadOnlyList<Address> Path { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="ExpertiseAggregationException"/>.
    /// </summary>
    public ExpertiseAggregationException(Address unit, IReadOnlyList<Address> path, string message)
        : base(message)
    {
        Unit = unit;
        Path = path;
    }
}