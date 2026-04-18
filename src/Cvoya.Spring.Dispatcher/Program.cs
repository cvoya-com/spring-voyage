// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dispatcher;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);

// Dispatcher options — per-worker bearer tokens + tenant scoping.
builder.Services.AddOptions<DispatcherOptions>()
    .BindConfiguration(DispatcherOptions.SectionName);

// Runtime options — the dispatcher owns the container binary locally. OSS
// ships podman only; downstream deployment repos can register alternative
// IContainerRuntime implementations before calling into the dispatcher host.
builder.Services.AddOptions<ContainerRuntimeOptions>()
    .BindConfiguration("ContainerRuntime");

// The dispatcher is the one place where the process container runtime lives.
// Workers never hold a ProcessContainerRuntime binding — they bind a
// DispatcherClientContainerRuntime instead (registered in the Dapr DI layer).
builder.Services.AddSingleton<IContainerRuntime, PodmanRuntime>();

// Bearer-token auth over DispatcherOptions.Tokens. Keeping the scheme minimal
// — a downstream host that targets multi-tenant K8s deployments can swap in a
// JWT / mTLS handler by registering a different default scheme before calling
// UseAuthentication.
builder.Services
    .AddAuthentication(BearerTokenAuthHandler.SchemeName)
    .AddScheme<BearerTokenAuthOptions, BearerTokenAuthHandler>(
        BearerTokenAuthHandler.SchemeName, _ => { });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Health endpoint — unauthenticated, mirrors the other hosts' /health
// convention for the deploy scripts' readiness probes.
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }))
    .WithName("Health");

app.MapContainerEndpoints();

await app.RunAsync();

/// <summary>
/// Partial class to enable WebApplicationFactory-based integration testing.
/// </summary>
public partial class Program;