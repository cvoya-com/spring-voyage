// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Units;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

/// <summary>
/// Pulls the unit's container image via
/// <see cref="IContainerRuntime.PullImageAsync(string, TimeSpan, CancellationToken)"/>
/// so the subsequent <see cref="RunContainerProbeActivity"/> calls can exec
/// against a resident image.
/// </summary>
/// <remarks>
/// Failure mapping: <see cref="TimeoutException"/> surfaces as
/// <see cref="UnitValidationCodes.ProbeTimeout"/>; any other exception surfaces
/// as <see cref="UnitValidationCodes.ImagePullFailed"/>. Images that pull but
/// then fail to start surface as
/// <see cref="UnitValidationCodes.ImageStartFailed"/> later, from
/// <see cref="RunContainerProbeActivity"/>, not here.
/// </remarks>
public class PullImageActivity(
    IContainerRuntime containerRuntime,
    ILoggerFactory loggerFactory)
    : WorkflowActivity<PullImageActivityInput, PullImageActivityOutput>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PullImageActivity>();

    /// <inheritdoc />
    public override async Task<PullImageActivityOutput> RunAsync(
        WorkflowActivityContext context, PullImageActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        _logger.LogInformation(
            "Pulling image {Image} with timeout {Timeout}", input.Image, input.Timeout);

        try
        {
            await containerRuntime.PullImageAsync(input.Image, input.Timeout);
            return new PullImageActivityOutput(Success: true, Failure: null);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Image pull timed out for {Image}", input.Image);
            return new PullImageActivityOutput(
                Success: false,
                Failure: new UnitValidationError(
                    Step: UnitValidationStep.PullingImage,
                    Code: UnitValidationCodes.ProbeTimeout,
                    Message: $"Image pull exceeded the configured timeout of {input.Timeout}.",
                    Details: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["image"] = input.Image,
                        ["timeout"] = input.Timeout.ToString(),
                    }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image pull failed for {Image}", input.Image);
            return new PullImageActivityOutput(
                Success: false,
                Failure: new UnitValidationError(
                    Step: UnitValidationStep.PullingImage,
                    Code: UnitValidationCodes.ImagePullFailed,
                    Message: $"Failed to pull image '{input.Image}': {ex.Message}",
                    Details: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["image"] = input.Image,
                        ["exception_type"] = ex.GetType().Name,
                    }));
        }
    }
}