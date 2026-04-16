// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Dapr.Execution;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

/// <summary>
/// Maps the Ollama model-discovery proxy endpoint. The UI calls this to
/// populate the model dropdown with models actually pulled on the
/// configured Ollama server. The endpoint is intentionally thin — no
/// caching, no auth on the Ollama side — because it proxies a single
/// GET and returns a simplified view.
/// </summary>
public static class OllamaEndpoints
{
    /// <summary>
    /// Registers Ollama endpoints on the specified endpoint route builder.
    /// </summary>
    public static RouteGroupBuilder MapOllamaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ollama")
            .WithTags("Ollama");

        group.MapGet("/models", ListModelsAsync)
            .WithName("ListOllamaModels")
            .WithSummary("List models pulled on the configured Ollama server")
            .Produces<OllamaModelInfo[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return group;
    }

    private static async Task<IResult> ListModelsAsync(
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaOptions> options,
        CancellationToken cancellationToken)
    {
        var baseUrl = options.Value.BaseUrl.TrimEnd('/');

        try
        {
            using var client = httpClientFactory.CreateClient("OllamaDiscovery");
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.Value.HealthCheckTimeoutSeconds);

            var response = await client.GetAsync("/api/tags", cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(
                OllamaJsonContext.Default.OllamaTagsResponse,
                cancellationToken);

            var models = (body?.Models ?? [])
                .Select(m => new OllamaModelInfo(m.Name, m.Size, m.ModifiedAt))
                .ToArray();

            return Results.Ok(models);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return Results.Problem(
                detail: $"Ollama server not reachable at {baseUrl}: {ex.Message}",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Ollama Unavailable");
        }
    }
}

/// <summary>
/// Simplified model info returned by the discovery endpoint.
/// </summary>
/// <param name="Name">The model tag (e.g. <c>llama3.2:3b</c>).</param>
/// <param name="Size">The model size in bytes.</param>
/// <param name="ModifiedAt">When the model was last modified/pulled.</param>
public record OllamaModelInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("modifiedAt")] string? ModifiedAt);

/// <summary>
/// Mirrors the shape of Ollama's <c>GET /api/tags</c> response.
/// </summary>
internal record OllamaTagsResponse(
    [property: JsonPropertyName("models")] OllamaTagModel[]? Models);

/// <summary>
/// Individual model entry in the Ollama <c>/api/tags</c> response.
/// </summary>
internal record OllamaTagModel(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("modified_at")] string? ModifiedAt);

[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSerializable(typeof(OllamaModelInfo[]))]
internal partial class OllamaJsonContext : JsonSerializerContext;