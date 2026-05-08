// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;

/// <summary>
/// Default <see cref="IOrchestrationToolProvider"/> — returns an empty
/// orchestration-tool surface for every (agent, thread) pair.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0039 ("units are agents") makes the orchestration-tool surface a
/// per-thread capability resolved for a given agent address rather than a
/// property of a separate Unit actor. The platform default is "no
/// orchestration tools" — appropriate for a leaf agent with no children —
/// and concrete providers (such as the directory-driven provider landed in
/// task D2) replace this default at host wire-up by registering before
/// <c>AddCvoyaSpring*</c>.
/// </para>
/// <para>
/// Internal because callers depend only on <see cref="IOrchestrationToolProvider"/>;
/// the class itself is an implementation detail of the Dapr host.
/// </para>
/// </remarks>
internal sealed class EmptyOrchestrationToolProvider : IOrchestrationToolProvider
{
    /// <inheritdoc />
    public OrchestrationToolDescriptor[] GetOrchestrationTools(Address agent, Guid threadId)
        => Array.Empty<OrchestrationToolDescriptor>();
}