// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using System.Text.Json.Serialization;

using Cvoya.Spring.Connector.GitHub.DependencyInjection;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Endpoints;
using Cvoya.Spring.Host.Api.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

var isLocalDev = args.Contains("--local") ||
    builder.Configuration.GetValue<bool>("LocalDev");

if (isLocalDev)
{
    builder.Configuration["LocalDev"] = "true";
}

builder.Services
    .AddCvoyaSpringCore()
    .AddCvoyaSpringDapr(builder.Configuration)
    .AddCvoyaSpringOllamaLlm(builder.Configuration)
    .AddCvoyaSpringConnectorGitHub(builder.Configuration)
    .AddCvoyaSpringApiServices(builder.Configuration);

// DataProtection tries to persist/load keys from disk and logs a warning when
// no stable key directory is configured. During build-time OpenAPI generation
// (GetDocument.Insider) this is pure noise. Skip registration when running
// under design-time tooling. See #370.
if (!BuildEnvironment.IsDesignTimeTooling)
{
    builder.Services.AddCvoyaSpringDataProtection(builder.Configuration);
}

if (isLocalDev)
{
    builder.Services.AddAuthentication(AuthConstants.LocalDevScheme)
        .AddScheme<AuthenticationSchemeOptions, LocalDevAuthHandler>(AuthConstants.LocalDevScheme, null);
}
else
{
    builder.Services.AddAuthentication(AuthConstants.ApiTokenScheme)
        .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthHandler>(AuthConstants.ApiTokenScheme, null);
}

builder.Services.AddAuthorization(options => options.AddUnitPermissionPolicies());
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

// Serialize every enum as its string name (case-insensitive on the way in)
// so clients get "Running" instead of 3 and don't have to reconstruct the
// numeric ordering. Individual endpoints no longer need to call .ToString().
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(allowIntegerValues: false));
});

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();

// .NET 10's native OpenAPI. The document is emitted as a build artefact
// via the Microsoft.Extensions.ApiDescription.Server package (configured
// in the csproj) so the web client's codegen reads from the committed
// JSON file rather than needing a running server. MapOpenApi still
// exposes /openapi/v1.json at runtime for introspection.
builder.Services.AddOpenApi("v1", options =>
{
    // `decimal` fields round-trip through JSON as plain numbers with our
    // default serialization, but the generator advertises them as
    // `["number", "string"]` to accommodate extreme-precision strings.
    // That poisons every TypeScript consumer (widening the field to
    // `string | number` — see #181). Tighten the contract to `number`
    // only; any client needing the full decimal precision would have
    // to opt in via a custom type.
    options.AddSchemaTransformer((schema, context, _) =>
    {
        var t = context.JsonTypeInfo.Type;
        if (t == typeof(decimal) || t == typeof(decimal?))
        {
            schema.Type = Microsoft.OpenApi.JsonSchemaType.Number;
            schema.Format = "double";
            schema.Pattern = null;
        }
        return Task.CompletedTask;
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment() || isLocalDev)
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }))
    .WithTags("Health")
    .WithName("Health")
    .ExcludeFromDescription();

app.MapAuthEndpoints();
app.MapAgentEndpoints().RequireAuthorization();
app.MapUnitEndpoints().RequireAuthorization();
app.MapUnitPolicyEndpoints().RequireAuthorization();
app.MapMembershipEndpoints().RequireAuthorization();
app.MapPackageEndpoints().RequireAuthorization();
app.MapMessageEndpoints().RequireAuthorization();
app.MapDirectoryEndpoints().RequireAuthorization();
app.MapCloneEndpoints().RequireAuthorization();
app.MapCostEndpoints().RequireAuthorization();
app.MapBudgetEndpoints().RequireAuthorization();
app.MapInitiativeEndpoints().RequireAuthorization();
app.MapActivityEndpoints().RequireAuthorization();
app.MapConversationEndpoints().RequireAuthorization();
app.MapInboxEndpoints().RequireAuthorization();
app.MapAnalyticsEndpoints().RequireAuthorization();
app.MapDashboardEndpoints().RequireAuthorization();
app.MapSkillsEndpoints().RequireAuthorization();
app.MapConnectorEndpoints();
app.MapSecretEndpoints().RequireAuthorization();
app.MapOllamaEndpoints().RequireAuthorization();
app.MapWebhookEndpoints();

await app.RunAsync();

/// <summary>
/// Partial class to enable WebApplicationFactory-based integration testing.
/// </summary>
public partial class Program;