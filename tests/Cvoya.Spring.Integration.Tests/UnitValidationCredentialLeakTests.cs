// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Collections.Concurrent;
using System.Text.Json;

using Cvoya.Spring.AgentRuntimes.OpenAI.DependencyInjection;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.AgentRuntimes;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Workflows.Activities;

using global::Dapr.Workflow;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Credential-leak canary for the T-04 in-container validation pipeline (#941).
/// Drives the real <see cref="RunContainerProbeActivity"/> chain end-to-end
/// against the real <see cref="OpenAiAgentRuntime"/> with a unique canary
/// credential, then asserts the canary appears nowhere on the outside: not
/// in the persisted <c>LastValidationErrorJson</c> column, not on the
/// DTO projection exposed by the activity, and not in any
/// <see cref="ActivityEventType.ValidationProgress"/> event emitted by
/// <see cref="EmitValidationProgressActivity"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Harness scope.</b> The test substitutes <see cref="IContainerRuntime"/>
/// with a canned 401-shape so Podman/Docker are not required in CI; every
/// other collaborator (runtime registry, probe-step activity, redaction
/// chain, EF-backed validation tracker, activity-event bus) is the real
/// implementation. This is the inverse of the sibling
/// <c>UnitValidationWorkflowTests</c>: that file exercises the workflow
/// body with mocked activities; this file exercises real activities + real
/// redaction chain. Together they cover the pipeline without a full stack.
/// </para>
/// <para>
/// <b>Dispatcher-log assertion.</b> Host / dispatcher stdout is captured
/// inside the substituted <see cref="IContainerRuntime"/> alone — the
/// activity redacts before interpretation and again on the way out, and
/// the canned stdout / stderr fed by the stub is an upper bound on what a
/// real dispatcher would surface. Expanding the assertion to cover a
/// separate dispatcher-log surface would require a log-capture channel
/// the harness does not expose today; not built for this test per T-09's
/// scope guard.
/// </para>
/// </remarks>
public sealed class UnitValidationCredentialLeakTests : IDisposable
{
    private readonly string _canary = $"SPRING_PROBE_CANARY_{Guid.NewGuid():N}";
    private readonly string _unitActorId = $"canary-unit-{Guid.NewGuid():N}";
    private readonly List<ActivityEvent> _emittedEvents = new();
    private readonly ServiceProvider _services;
    private readonly CannedForbiddenContainerRuntime _containerRuntime;

    public UnitValidationCredentialLeakTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Real runtime registry + OpenAI runtime. OpenAI's ValidatingCredential
        // probe shells `curl -H "Authorization: Bearer …" /v1/models` — when
        // the provider returns 401, the runtime's interpreter maps to
        // CredentialInvalid, which is the failure path this canary asserts.
        services.AddCvoyaSpringAgentRuntimeOpenAI();
        services.TryAddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();

        // Substitute the container boundary. The activity redacts BEFORE
        // interpretation, so a realistic canned stdout / stderr carrying the
        // canary is the right shape for this test.
        _containerRuntime = new CannedForbiddenContainerRuntime(_canary);
        services.AddSingleton<IContainerRuntime>(_containerRuntime);

        // Recording activity bus — captures every ValidationProgress event
        // the canary path emits so the test can assert none carry the canary.
        services.AddSingleton<IActivityEventBus>(new RecordingActivityEventBus(_emittedEvents));

        // Real EF DB so the DbUnitValidationTracker write + the GET read
        // path both hit a real row. The row's LastValidationErrorJson column
        // is where this test's primary assertion lives.
        var dbName = $"CanaryDb_{Guid.NewGuid():N}";
        services.AddDbContext<SpringDbContext>(opts => opts.UseInMemoryDatabase(dbName));
        services.TryAddSingleton<IUnitValidationTracker, DbUnitValidationTracker>();

        // The activities take a WorkflowActivityContext parameter but the
        // default substitute works — the concrete type is never consulted by
        // the implementations. Register them so DI resolves constructor args.
        services.AddTransient<RunContainerProbeActivity>();
        services.AddTransient<EmitValidationProgressActivity>();

        _services = services.BuildServiceProvider();

        // Seed the UnitDefinition row so DbUnitValidationTracker.SetFailureAsync
        // has a target to write to (production bootstrap creates this at
        // unit-create time; the canary shortcuts to the failure path).
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.UnitDefinitions.Add(new UnitDefinitionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = "default",
            UnitId = _unitActorId,
            ActorId = _unitActorId,
            Name = _unitActorId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastValidationRunId = "canary-run-id",
        });
        db.SaveChanges();
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task ValidatingCredential_OnProvider401_DoesNotLeakCanaryAnywhere()
    {
        var ct = TestContext.Current.CancellationToken;

        var runContainerProbe = _services.GetRequiredService<RunContainerProbeActivity>();
        var emitProgress = _services.GetRequiredService<EmitValidationProgressActivity>();
        var tracker = _services.GetRequiredService<IUnitValidationTracker>();
        var unitName = _unitActorId;

        // Mirror the workflow's emit-before-run order: a Running progress
        // event lands for ValidatingCredential, then the probe runs.
        await emitProgress.RunAsync(
            context: null!,
            new EmitValidationProgressActivityInput(
                UnitName: unitName,
                Step: UnitValidationStep.ValidatingCredential,
                Status: "Running",
                Code: null));

        // Drive the real activity. The substituted IContainerRuntime returns
        // ExitCode=1 with a canary-containing stdout/stderr; the redactor
        // scrubs the canary before interpretation and again on the way out.
        var probeOutput = await runContainerProbe.RunAsync(
            context: null!,
            new RunContainerProbeActivityInput(
                RuntimeId: "openai",
                Step: UnitValidationStep.ValidatingCredential,
                Image: "ghcr.io/example/unit-runtime:test",
                Credential: _canary,
                RequestedModel: "gpt-4o"));

        // Emit the Failed progress event the workflow would post next.
        await emitProgress.RunAsync(
            context: null!,
            new EmitValidationProgressActivityInput(
                UnitName: unitName,
                Step: UnitValidationStep.ValidatingCredential,
                Status: "Failed",
                Code: probeOutput.Failure?.Code));

        // Persist the failure payload the way UnitActor.CompleteValidationAsync
        // would when the workflow posts the terminal callback.
        var errorJson = probeOutput.Failure is null
            ? null
            : JsonSerializer.Serialize(probeOutput.Failure);
        await tracker.SetFailureAsync(_unitActorId, errorJson, ct);

        // ── Sanity: the failure path actually ran as expected. ─────────
        probeOutput.Success.ShouldBeFalse();
        probeOutput.Failure.ShouldNotBeNull();
        probeOutput.Failure!.Code.ShouldBe(UnitValidationCodes.CredentialInvalid);

        // ── The canary must appear nowhere observable. ─────────────────

        // 1. Persisted row — LastValidationErrorJson.
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var row = await db.UnitDefinitions
                .AsNoTracking()
                .FirstAsync(u => u.ActorId == _unitActorId, ct);

            row.LastValidationErrorJson.ShouldNotBeNull();
            row.LastValidationErrorJson!.ShouldNotContain(_canary);
        }

        // 2. DTO projection — UnitValidationError's Message + Details values.
        probeOutput.Failure.Message.ShouldNotBeNull();
        probeOutput.Failure.Message.ShouldNotContain(_canary);
        if (probeOutput.Failure.Details is not null)
        {
            foreach (var (key, value) in probeOutput.Failure.Details)
            {
                key.ShouldNotContain(_canary);
                value.ShouldNotContain(_canary);
            }
        }

        // 3. The redacted stdout/stderr pair the activity returns (this is
        // the value any downstream log enricher would have access to).
        probeOutput.RedactedStdOut.ShouldNotContain(_canary);
        probeOutput.RedactedStdErr.ShouldNotContain(_canary);

        // 4. Activity-event stream — every ValidationProgress event's
        // Summary + Details payload.
        _emittedEvents.ShouldNotBeEmpty();
        foreach (var evt in _emittedEvents)
        {
            evt.Summary.ShouldNotContain(_canary);
            if (evt.Details is { } details)
            {
                details.GetRawText().ShouldNotContain(_canary);
            }
        }

        // 5. Test-setup sanity: the canary WAS presented to the container
        // boundary in the env block (matching the OpenAI probe's design —
        // command references `$SPRING_CREDENTIAL`, env carries the value).
        // If this fails the test is not actually exercising the redaction
        // chain and the other assertions are false greens.
        _containerRuntime.LastEnv.ShouldNotBeNull();
        _containerRuntime.LastEnv!.Values.ShouldContain(_canary);
    }

    /// <summary>
    /// <see cref="IContainerRuntime"/> stub that returns a 401-shaped
    /// response for the <see cref="UnitValidationStep.ValidatingCredential"/>
    /// probe. Captures the last command it saw so the test can verify the
    /// canary was actually presented to the container boundary (otherwise a
    /// broken test setup could pass trivially). The 401 stdout includes the
    /// canary so we know the downstream redactor is doing its job.
    /// </summary>
    private sealed class CannedForbiddenContainerRuntime : IContainerRuntime
    {
        private readonly string _canary;

        public string LastCommand { get; private set; } = string.Empty;
        public IReadOnlyDictionary<string, string>? LastEnv { get; private set; }

        public CannedForbiddenContainerRuntime(string canary)
        {
            _canary = canary;
        }

        public Task<ContainerResult> RunAsync(ContainerConfig config, CancellationToken ct = default)
        {
            // Command is now a list (#1093); join for diagnostic display only.
            // We never assert on the joined form for credential redaction —
            // the canary lives in stderr — so the lossy join here is fine.
            LastCommand = config.Command is null
                ? string.Empty
                : string.Join(' ', config.Command);
            LastEnv = config.EnvironmentVariables;

            // The OpenAI ValidatingCredential probe is
            //   curl -sS -o /dev/null -w '%{http_code}' -H 'Authorization: Bearer $SPRING_CREDENTIAL' …
            // so in production stdout is just the status digits. Stderr is
            // the realistic leak surface — curl's own error prints,
            // dispatcher-level diagnostics, container runtime chatter — any
            // of which could echo the env var's value. We plant the canary
            // in stderr so the redactor's contract ("scrub stderr before
            // interpretation AND before it leaves the activity") is what
            // this test actually exercises.
            var stderr = $"curl: (55) authentication failed: key='{_canary}'";
            return Task.FromResult(new ContainerResult(
                ContainerId: "canary-container",
                ExitCode: 0,
                StandardOutput: "401",
                StandardError: stderr));
        }

        public Task PullImageAsync(string image, TimeSpan timeout, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> StartAsync(ContainerConfig config, CancellationToken ct = default)
            => throw new NotSupportedException("canary harness does not start detached containers");

        public Task StopAsync(string containerId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> GetLogsAsync(string containerId, int tail = 200, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        // Stage 2 (#522) added network + container-probe ops to
        // IContainerRuntime. The canary doesn't use them, but the harness
        // still has to satisfy the interface. Throw on the surfaces a
        // future test could accidentally route through this stub so a
        // silent no-op never masks a regression.
        public Task CreateNetworkAsync(string name, CancellationToken ct = default)
            => throw new NotSupportedException("canary harness does not create networks");

        public Task RemoveNetworkAsync(string name, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> ProbeContainerHttpAsync(string containerId, string url, CancellationToken ct = default)
            => throw new NotSupportedException("canary harness does not probe containers");

        // #1175: host-side HTTP probe (avoids in-container wget dependency).
        // Same shape as ProbeContainerHttpAsync above.
        public Task<bool> ProbeHttpFromHostAsync(string containerId, string url, CancellationToken ct = default)
            => throw new NotSupportedException("canary harness does not probe containers from host");

        // The transient-container probe primitive (#1197 follow-up) is the
        // distroless-sidecar escape hatch from ProbeContainerHttpAsync.
        // The canary doesn't spin up daprd sidecars, so the honest answer
        // is the same shape as the per-container probe above.
        public Task<bool> ProbeHttpFromTransientContainerAsync(
            string probeImage, string network, string url, CancellationToken ct = default)
            => throw new NotSupportedException("canary harness does not run transient probes");

        // #1160 added the dispatcher-proxied A2A primitive. The canary
        // test runs the OpenAI ValidatingCredential probe (a one-shot
        // RunAsync) and never exercises agent A2A traffic, so the
        // honest answer is "not supported here" — the same shape as the
        // other surfaces above.
        public Task<ContainerHttpResponse> SendHttpJsonAsync(
            string containerId, string url, byte[] body, CancellationToken ct = default)
            => throw new NotSupportedException("canary harness does not proxy A2A messages");

        // D3c (#1274): volume ops. The canary never provisions agent workspaces,
        // so all three surfaces throw so a future test routing through this stub
        // doesn't silently succeed.
        public Task EnsureVolumeAsync(string volumeName, CancellationToken ct = default)
            => throw new NotSupportedException("canary harness does not provision volumes");

        public Task RemoveVolumeAsync(string volumeName, CancellationToken ct = default)
            => throw new NotSupportedException("canary harness does not remove volumes");

        public Task<VolumeMetrics?> GetVolumeMetricsAsync(string volumeName, CancellationToken ct = default)
            => throw new NotSupportedException("canary harness does not query volume metrics");
    }

    /// <summary>
    /// Records every <see cref="ActivityEvent"/> published through the bus.
    /// The canary test only inspects what was published after the probe
    /// completes; <see cref="ActivityStream"/> is wired to an Rx subject so
    /// any observer (unused by this test) sees the same sequence.
    /// </summary>
    private sealed class RecordingActivityEventBus : IActivityEventBus
    {
        private readonly List<ActivityEvent> _events;
        private readonly HotObservable _observable = new();

        public RecordingActivityEventBus(List<ActivityEvent> events)
        {
            _events = events;
        }

        public IObservable<ActivityEvent> ActivityStream => _observable;

        public Task PublishAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            lock (_events)
            {
                _events.Add(activityEvent);
            }
            _observable.Emit(activityEvent);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Minimal hot observable — just enough to satisfy
        /// <see cref="IActivityObservable.ActivityStream"/>. No subscribers
        /// are attached in the canary test, so the observer list stays empty;
        /// but a hypothetical subscription semantic is preserved so the
        /// test harness can't mask a bug where a consumer reads the stream.
        /// </summary>
        private sealed class HotObservable : IObservable<ActivityEvent>
        {
            private readonly ConcurrentBag<IObserver<ActivityEvent>> _observers = new();

            public IDisposable Subscribe(IObserver<ActivityEvent> observer)
            {
                _observers.Add(observer);
                return new Unsubscribe(_observers, observer);
            }

            public void Emit(ActivityEvent evt)
            {
                foreach (var observer in _observers)
                {
                    try { observer.OnNext(evt); }
                    catch { /* test harness */ }
                }
            }

            private sealed class Unsubscribe(
                ConcurrentBag<IObserver<ActivityEvent>> pool,
                IObserver<ActivityEvent> observer) : IDisposable
            {
                public void Dispose()
                {
                    // ConcurrentBag doesn't support remove; accept the leak
                    // for test duration — observer lifetime == test lifetime.
                    _ = pool;
                    _ = observer;
                }
            }
        }
    }
}