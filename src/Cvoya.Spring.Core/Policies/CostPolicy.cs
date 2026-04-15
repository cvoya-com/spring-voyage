// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

/// <summary>
/// Cost ceilings applied to agents in a unit. Third concrete
/// <see cref="UnitPolicy"/> dimension — see #248. Caps are numeric upper
/// bounds in the same currency unit that <c>CostRecord.Cost</c> is measured in
/// (USD in the OSS default). A <c>null</c> field means "no cap along that
/// window".
/// </summary>
/// <remarks>
/// <para>
/// Evaluation is pre-call: the enforcer compares the agent's accumulated spend
/// within the relevant rolling window against the corresponding cap. When a
/// cap is non-<c>null</c> and already met (or would be exceeded by
/// <c>projectedCost</c>) the call is denied and the decision carries the
/// current window usage so operators can surface the overrun to agents without
/// re-running the query.
/// </para>
/// <para>
/// <paramref name="MaxCostPerInvocation"/> is a per-call absolute cap — a
/// single model turn whose <c>projectedCost</c> exceeds it is denied even when
/// the agent has plenty of daily / hourly budget left. This is useful for
/// catching runaway prompt sizes before the model call is made.
/// </para>
/// </remarks>
/// <param name="MaxCostPerInvocation">
/// Optional absolute cap on the cost of a single invocation. Denies any call
/// whose projected cost strictly exceeds this value.
/// </param>
/// <param name="MaxCostPerHour">
/// Optional rolling-hour cap. Denies a call when the sum of recent (last hour)
/// costs plus <c>projectedCost</c> would strictly exceed this value.
/// </param>
/// <param name="MaxCostPerDay">
/// Optional rolling-24-hour cap. Denies a call when the sum of recent (last
/// 24h) costs plus <c>projectedCost</c> would strictly exceed this value.
/// </param>
public record CostPolicy(
    decimal? MaxCostPerInvocation = null,
    decimal? MaxCostPerHour = null,
    decimal? MaxCostPerDay = null);