// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using System.Reflection;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dispatcher;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;

// `--version` short-circuit: deployment/spring-voyage-host.sh's `status`
// subcommand calls this to display the running dispatcher's version next
// to its PID and bind URL. Returning early avoids spinning up the web
// host just to print a string. `-v` is accepted as the short alias.
if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
{
    var info = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "unknown";
    Console.WriteLine(info);
    return;
}

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

// Workspace materialiser: per-invocation agent workspaces are written here on
// the dispatcher host, then bind-mounted into the agent container. Lives in
// the dispatcher (not the worker) so the host's container runtime sees the
// directory. See issue #1042.
builder.Services.AddSingleton<IWorkspaceMaterializer, WorkspaceMaterializer>();

// Startup probe: the configured container runtime binary must resolve on PATH
// before the dispatcher accepts requests. Without this, a misconfigured image
// (e.g. one that ships `podman-remote` but not `podman`) comes up healthy and
// then 500s on every dispatch with "No such file or directory". See #984.
builder.Services.TryAddSingleton<IContainerRuntimeBinaryProbe, ContainerRuntimeBinaryProbe>();
builder.Services.TryAddSingleton<IWorkspaceRootProbe, WorkspaceRootProbe>();
builder.Services.AddCvoyaSpringConfigurationValidator();
builder.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IConfigurationRequirement, ContainerRuntimeBinaryConfigurationRequirement>());
builder.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IConfigurationRequirement, WorkspaceRootConfigurationRequirement>());

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