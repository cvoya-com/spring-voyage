// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Surfaces the orchestration tools an agent may invoke against its child
/// composition (children, fan-out, status queries) within a given thread.
///
/// Introduced by ADR-0039 ("units are agents"): the orchestration tool
/// surface is no longer a property of a separate `Unit` actor type — it is
/// a per-thread capability resolved for a given agent address. A1 lands
/// the interface; later tasks wire concrete providers and call sites.
/// </summary>
/// <remarks>
/// <para>
/// Implementations describe the tools available — not invoke them. The
/// returned <see cref="OrchestrationToolDescriptor"/> array carries the
/// canonical <see cref="OrchestrationToolName"/> and the JSON Schemas the
/// caller advertises to the agent runtime. Empty array is a valid result
/// (a leaf agent has no children to orchestrate).
/// </para>
/// <para>
/// The <paramref name="threadId"/> parameter is part of the contract so a
/// provider can scope tool availability to a particular conversation —
/// e.g. when the set of children differs per thread, or when a thread is
/// configured to disable fan-out. Providers that do not need
/// thread-scoping ignore the parameter.
/// </para>
/// </remarks>
public interface IOrchestrationToolProvider
{
    /// <summary>
    /// Returns the orchestration tools available to <paramref name="agent"/>
    /// within thread <paramref name="threadId"/>. Returns an empty array
    /// when the agent has no orchestration surface (e.g. a leaf agent
    /// with no children).
    /// </summary>
    /// <param name="agent">The address of the agent whose orchestration tools are being requested.</param>
    /// <param name="threadId">The conversation thread the tools are scoped to.</param>
    OrchestrationToolDescriptor[] GetOrchestrationTools(Address agent, Guid threadId);
}
