// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Worker.Composition;

using Cvoya.Spring.Connector.Arxiv.DependencyInjection;
using Cvoya.Spring.Connector.GitHub.DependencyInjection;
using Cvoya.Spring.Connector.WebSearch.DependencyInjection;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;
using global::Dapr.Workflow;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Central composition helper for the Worker host's dependency-injection graph.
/// </summary>
/// <remarks>
/// <para>
/// The Worker's <c>Program.cs</c> used to inline every <c>Add…</c> call, which
/// made the container graph impossible to exercise from a test project: the
/// only entry point was <c>await app.RunAsync()</c>, which also bound a port,
/// registered signal handlers, and mapped actor endpoints. That meant a
/// DI registration gap — the constructor of a skill or actor asking for a
/// service that no host had registered — was invisible to <c>dotnet build</c>
/// and <c>dotnet test</c>, and only surfaced at container startup. See
/// <see href="https://github.com/cvoya-com/spring-voyage/issues/586">#586</see>
/// and its motivating incident on
/// <see href="https://github.com/cvoya-com/spring-voyage/pull/588">#588</see>.
/// </para>
/// <para>
/// Extracting the service-registration portion into <see cref="AddWorkerServices"/>
/// gives the Worker host one call site and lets the Worker composition smoke
/// test ride the exact same graph — no reconstructed list that drifts from
/// <c>Program.cs</c> the first time we add a connector or workflow.
/// </para>
/// <para>
/// Only service-registration lives here. Signal-handler wiring, HTTP endpoint
/// mapping, and <c>WebApplication</c> lifecycle (<c>Build</c>/<c>RunAsync</c>)
/// stay in <c>Program.cs</c> because they are specific to running the host,
/// not to constructing the container.
/// </para>
/// </remarks>
public static class WorkerComposition
{
    /// <summary>
    /// Registers every Worker-owned service on <paramref name="services"/>.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration, forwarded to the Spring DI extensions.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddWorkerServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Register Spring services
        services
            .AddCvoyaSpringCore()
            .AddCvoyaSpringDapr(configuration)
            .AddCvoyaSpringOllamaLlm(configuration)
            .AddCvoyaSpringConnectorGitHub(configuration)
            .AddCvoyaSpringConnectorArxiv(configuration)
            .AddCvoyaSpringConnectorWebSearch(configuration);

        // DataProtection registration is gated by design-time tooling to avoid
        // noisy ephemeral-key warnings during build-time OpenAPI generation. The
        // Worker does not generate OpenAPI docs itself, but shares the same DI
        // setup path via AddCvoyaSpringDapr — gate defensively. See #370.
        if (!BuildEnvironment.IsDesignTimeTooling)
        {
            services.AddCvoyaSpringDataProtection(configuration);
        }

        // Worker owns EF Core migrations. The API host intentionally does NOT
        // register DatabaseMigrator: when both hosts ran it concurrently they
        // raced on DDL and one crashed with `42P07: relation already exists`
        // (issue #305). Registering here keeps automatic schema upgrades on
        // fresh deployments while making the Worker the single owner.
        services.AddCvoyaSpringDatabaseMigrator();

        // Register Dapr workflows
        services.AddDaprWorkflow(options =>
        {
            options.RegisterWorkflow<AgentLifecycleWorkflow>();
            options.RegisterWorkflow<CloningLifecycleWorkflow>();
            options.RegisterActivity<ValidateAgentDefinitionActivity>();
            options.RegisterActivity<RegisterAgentActivity>();
            options.RegisterActivity<UnregisterAgentActivity>();
            options.RegisterActivity<ValidateCloneRequestActivity>();
            options.RegisterActivity<CreateCloneActorActivity>();
            options.RegisterActivity<RegisterCloneActivity>();
            options.RegisterActivity<DestroyCloneActivity>();
        });

        // Register Dapr actors
        services.AddActors(options =>
        {
            options.Actors.RegisterActor<AgentActor>();
            options.Actors.RegisterActor<UnitActor>();
            options.Actors.RegisterActor<ConnectorActor>();
            options.Actors.RegisterActor<HumanActor>();

            options.ActorIdleTimeout = TimeSpan.FromHours(1);
            options.ActorScanInterval = TimeSpan.FromSeconds(30);
            options.ReentrancyConfig = new ActorReentrancyConfig { Enabled = false };

            // Match the caller-side actor-proxy configuration: JSON
            // serialization with a JsonElement converter that clones into a
            // self-contained document. Dapr's default DataContract
            // serializer cannot round-trip Message.Payload (a JsonElement)
            // losslessly — it deserialises into default(JsonElement), whose
            // _parent is null, and every subsequent read throws "Operation
            // is not valid due to the current state of the object". Both
            // ends of the remoting pipe must agree on JSON for the wire
            // format to be honoured; see ActorRemotingJsonOptions.
            options.UseJsonSerialization = true;
            options.JsonSerializerOptions = ActorRemotingJsonOptions.Instance;
        });

        return services;
    }
}