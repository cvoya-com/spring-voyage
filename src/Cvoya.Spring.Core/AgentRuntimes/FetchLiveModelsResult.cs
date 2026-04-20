// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// The result of a live model-catalog fetch against a runtime's backing
/// service. Returned by
/// <see cref="IAgentRuntime.FetchLiveModelsAsync(string, System.Threading.CancellationToken)"/>.
/// </summary>
/// <param name="Status">
/// Raw outcome of this fetch attempt. Callers MUST inspect this before
/// reading <paramref name="Models"/> — only <see cref="FetchLiveModelsStatus.Success"/>
/// carries a meaningful list.
/// </param>
/// <param name="Models">
/// The live model list returned by the backing service when
/// <paramref name="Status"/> is <see cref="FetchLiveModelsStatus.Success"/>;
/// an empty array for every other outcome. Implementations should surface
/// the same projection shape as <see cref="IAgentRuntime.DefaultModels"/>
/// so callers can swap the two lists without adapter code.
/// </param>
/// <param name="ErrorMessage">
/// Human-readable explanation when the fetch did not complete successfully.
/// <c>null</c> on <see cref="FetchLiveModelsStatus.Success"/>.
/// </param>
public sealed record FetchLiveModelsResult(
    FetchLiveModelsStatus Status,
    IReadOnlyList<ModelDescriptor> Models,
    string? ErrorMessage)
{
    /// <summary>Convenience factory for a successful fetch with the supplied model list.</summary>
    /// <param name="models">Live models returned by the backing service.</param>
    public static FetchLiveModelsResult Success(IReadOnlyList<ModelDescriptor> models) =>
        new(FetchLiveModelsStatus.Success, models, ErrorMessage: null);

    /// <summary>Convenience factory for a runtime that cannot enumerate live models.</summary>
    /// <param name="reason">Operator-readable description of why the runtime is unsupported.</param>
    public static FetchLiveModelsResult Unsupported(string reason) =>
        new(FetchLiveModelsStatus.Unsupported, Array.Empty<ModelDescriptor>(), reason);

    /// <summary>Convenience factory for a transport-layer failure.</summary>
    /// <param name="reason">Operator-readable description of the network failure.</param>
    public static FetchLiveModelsResult NetworkError(string reason) =>
        new(FetchLiveModelsStatus.NetworkError, Array.Empty<ModelDescriptor>(), reason);

    /// <summary>Convenience factory for a rejected credential.</summary>
    /// <param name="reason">Operator-readable description of the rejection.</param>
    public static FetchLiveModelsResult InvalidCredential(string reason) =>
        new(FetchLiveModelsStatus.InvalidCredential, Array.Empty<ModelDescriptor>(), reason);
}