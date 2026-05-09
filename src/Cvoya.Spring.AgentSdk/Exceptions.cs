// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

public sealed class OrchestrationAuthException : Exception
{
    public OrchestrationAuthException(string message)
        : base(message)
    {
    }

    public OrchestrationAuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class OrchestrationTransportException : Exception
{
    public OrchestrationTransportException(string message)
        : base(message)
    {
    }

    public OrchestrationTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MissingCallbackEnvironmentException : Exception
{
    public MissingCallbackEnvironmentException(string variableName)
        : base($"Required Spring Voyage callback environment variable '{variableName}' is missing.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);

        VariableName = variableName;
    }

    public string VariableName { get; }
}