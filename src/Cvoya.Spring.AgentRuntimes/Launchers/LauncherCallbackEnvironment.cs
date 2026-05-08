// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Launchers;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;

internal static class LauncherCallbackEnvironment
{
    public static void Add(
        IAgentCallbackEnvironmentBuilder? builder,
        AgentLaunchContext context,
        IDictionary<string, string> envVars)
    {
        if (builder is null)
        {
            throw new SpringException(
                "IAgentCallbackEnvironmentBuilder is not registered; launchers cannot inject " +
                $"{AgentCallbackEnvironmentContract.CallbackUrlEnvVar} and " +
                $"{AgentCallbackEnvironmentContract.CallbackTokenEnvVar}.");
        }

        builder.AddCallbackEnvironment(context, envVars);
    }
}