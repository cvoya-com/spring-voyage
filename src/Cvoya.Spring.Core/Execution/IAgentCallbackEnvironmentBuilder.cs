// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Adds the uniform callback env vars every runtime launcher must stamp onto
/// the container at launch time.
/// </summary>
public interface IAgentCallbackEnvironmentBuilder
{
    /// <summary>
    /// Adds <c>SPRING_CALLBACK_URL</c> and <c>SPRING_CALLBACK_TOKEN</c> to the
    /// supplied environment dictionary.
    /// </summary>
    /// <param name="context">Launch context for the invocation being prepared.</param>
    /// <param name="environmentVariables">Mutable env-var dictionary from the launcher.</param>
    void AddCallbackEnvironment(
        AgentLaunchContext context,
        IDictionary<string, string> environmentVariables);
}