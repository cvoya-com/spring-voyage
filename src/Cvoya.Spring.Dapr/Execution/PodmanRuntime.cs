/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

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
