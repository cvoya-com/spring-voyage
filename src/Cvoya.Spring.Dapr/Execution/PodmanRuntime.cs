// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Container runtime implementation that uses Podman to execute containers.
/// </summary>
public class PodmanRuntime(
    IOptions<ContainerRuntimeOptions> options,
    ILoggerFactory loggerFactory)
    : ProcessContainerRuntime("podman", options, loggerFactory);