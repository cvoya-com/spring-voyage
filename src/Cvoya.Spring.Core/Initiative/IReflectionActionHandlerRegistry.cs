// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

/// <summary>
/// Lookup seam that maps a <see cref="ReflectionOutcome.ActionType"/> string
/// to the registered <see cref="IReflectionActionHandler"/>. Factored out so
/// the actor depends on a narrow interface rather than on the concrete list
/// of handlers — and so the private cloud repo can swap in a richer registry
/// (audit-logging, tenant-scoped, feature-flag-gated) via DI.
/// </summary>
public interface IReflectionActionHandlerRegistry
{
    /// <summary>
    /// Returns the handler registered for the given action type, or <c>null</c>
    /// when no handler matches. Matching is case-insensitive.
    /// </summary>
    /// <param name="actionType">The action-type string to look up.</param>
    IReflectionActionHandler? Find(string? actionType);
}