// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using System.Reflection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dispatcher.Auth;

using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        // `--version` short-circuit: eng/deploy/spring-voyage-host.sh's `status`
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
        builder.Services.AddOptions<CallbackTokenOptions>()
            .BindConfiguration(CallbackTokenOptions.SectionName);

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
        // Cwd probe — keeps the dispatcher from silently starting in a deleted
        // working directory (e.g. a git worktree that was removed while the
        // dispatcher was still running). See issue #1674.
        builder.Services.TryAddSingleton<IDispatcherCwdProbe, DispatcherCwdProbe>();
        builder.Services.AddCvoyaSpringConfigurationValidator();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigurationRequirement, ContainerRuntimeBinaryConfigurationRequirement>());
        // Stage 2 of #522 / #1063: ContainerRuntimeConfigurationRequirement now lives
        // here too. The worker dropped its registration when its container CLI
        // binding moved through the dispatcher; the dispatcher is the only host that
        // reads ContainerRuntime:RuntimeType, so it owns validating it.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigurationRequirement, ContainerRuntimeConfigurationRequirement>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigurationRequirement, WorkspaceRootConfigurationRequirement>());
        // Cwd fail-fast — mandatory tier-1 requirement that aborts boot when the
        // dispatcher's working directory is unreachable (#1674). Paired with the
        // error-translation path in ProcessContainerRuntime.RunProcessAsync so a
        // cwd loss during the host's lifetime also surfaces with a precise
        // diagnostic instead of the generic "Unable to find the specified file".
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigurationRequirement, DispatcherCwdConfigurationRequirement>());

        builder.Services.TryAddSingleton<ITenantSigningKeyProvider, DispatcherTenantSigningKeyProvider>();
        builder.Services.TryAddSingleton<CallbackTokenValidator>();
        builder.Services.TryAddSingleton<IActorProxyFactory>(_ => new ActorProxyFactory(
            new ActorProxyOptions
            {
                UseJsonSerialization = true,
                JsonSerializerOptions = ActorRemotingJsonOptions.Instance,
            }));
        builder.Services.TryAddSingleton<IAgentProxyResolver, AgentProxyResolver>();
        builder.Services.TryAddSingleton<OrchestrationDepthCounter>();
        builder.Services.TryAddSingleton<ActivityEventBus>();
        builder.Services.TryAddSingleton<IActivityEventBus>(sp => sp.GetRequiredService<ActivityEventBus>());
        // ADR-0039 §3 gate 6 — single-tenant resolver is the OSS default;
        // a cloud overlay can substitute a tenant-aware variant via the
        // standard TryAdd seam without touching this registration.
        builder.Services.TryAddSingleton<IOrchestrationTenantResolver, SingleTenantOrchestrationTenantResolver>();
        builder.Services.TryAddSingleton<OrchestrationToolHandlers>();
        // The MCP orchestration handler (POST /v1/runtime/orchestration)
        // resolves the tool descriptors for its tools/list response from
        // IOrchestrationToolProvider. The dispatcher previously had no need
        // for this binding — the REST sub-routes do not advertise schemas —
        // so the default directory provider is registered here. A cloud
        // overlay can substitute a tenant-aware provider via the TryAdd seam.
        builder.Services.TryAddSingleton<IOrchestrationToolProvider, DirectoryOrchestrationToolProvider>();

        // Named HttpClient used by /v1/llm/forward and /v1/llm/forward/stream
        // to dispatch the upstream LLM call from the dispatcher process. We set
        // Timeout to InfiniteTimeSpan because long completions and streaming
        // SSE sessions can legitimately outlive the BCL default of 100s; the
        // per-request deadline is owned by the worker (it sets the request
        // CancellationToken on the way in). Closes #1168.
        builder.Services.AddHttpClient(LlmEndpoints.ForwardingHttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

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
        app.MapNetworkEndpoints();
        app.MapVolumeEndpoints();
        app.MapImageEndpoints();
        app.MapLlmEndpoints();
        app.MapOrchestrationCallbackEndpoints();

        await app.RunAsync();
    }
}
