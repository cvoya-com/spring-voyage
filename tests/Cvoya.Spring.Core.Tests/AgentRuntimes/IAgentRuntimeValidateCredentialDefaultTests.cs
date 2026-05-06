// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.AgentRuntimes;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Units;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the behaviour of the default
/// <see cref="IAgentRuntime.ValidateCredentialAsync(string, CancellationToken)"/>
/// method introduced for #1066. The default delegates to
/// <see cref="IAgentRuntime.FetchLiveModelsAsync(string, CancellationToken)"/>
/// so runtimes that already speak the live-catalog probe pick up a working
/// credential-validation surface without re-implementing transport — these
/// tests guard the four status mappings (Success / InvalidCredential /
/// NetworkError / Unsupported) and the defensive throw-to-NetworkError
/// translation that keeps the host endpoint from 500ing on a buggy plugin.
/// </summary>
public class IAgentRuntimeValidateCredentialDefaultTests
{
    [Fact]
    public async Task Default_Maps_Success_To_Valid_With_Timestamp()
    {
        // C# default interface methods must be invoked via an
        // interface-typed reference — calling them through the concrete
        // type bypasses the default. We therefore type the local as
        // IAgentRuntime so each test exercises the default impl rather
        // than any accidental override on FakeRuntime.
        IAgentRuntime runtime = new FakeRuntime(
            FetchLiveModelsResult.Success(Array.Empty<ModelDescriptor>()));

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var result = await runtime.ValidateCredentialAsync("sk-good", TestContext.Current.CancellationToken);
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        result.Valid.ShouldBeTrue();
        result.Status.ShouldBe(CredentialValidationStatus.Valid);
        result.ErrorMessage.ShouldBeNull();
        result.ValidatedAt.ShouldNotBeNull();
        result.ValidatedAt!.Value.ShouldBeInRange(before, after);
    }

    [Fact]
    public async Task Default_Maps_InvalidCredential_To_Invalid()
    {
        IAgentRuntime runtime = new FakeRuntime(FetchLiveModelsResult.InvalidCredential("rejected by upstream"));

        var result = await runtime.ValidateCredentialAsync("sk-bad", TestContext.Current.CancellationToken);

        result.Valid.ShouldBeFalse();
        result.Status.ShouldBe(CredentialValidationStatus.Invalid);
        result.ErrorMessage.ShouldBe("rejected by upstream");
    }

    [Fact]
    public async Task Default_Maps_NetworkError_To_NetworkError()
    {
        IAgentRuntime runtime = new FakeRuntime(FetchLiveModelsResult.NetworkError("DNS failure"));

        var result = await runtime.ValidateCredentialAsync("sk-anything", TestContext.Current.CancellationToken);

        result.Valid.ShouldBeFalse();
        result.Status.ShouldBe(CredentialValidationStatus.NetworkError);
        result.ErrorMessage.ShouldBe("DNS failure");
    }

    [Fact]
    public async Task Default_Maps_Unsupported_To_Unknown()
    {
        // Runtimes whose backing service does not expose a model-enumeration
        // endpoint (e.g. single-model providers) surface as Unknown so the
        // credential-health row stays untouched rather than incorrectly
        // flipping to Invalid.
        IAgentRuntime runtime = new FakeRuntime(FetchLiveModelsResult.Unsupported("no /v1/models"));

        var result = await runtime.ValidateCredentialAsync("sk-anything", TestContext.Current.CancellationToken);

        result.Valid.ShouldBeFalse();
        result.Status.ShouldBe(CredentialValidationStatus.Unknown);
        result.ErrorMessage.ShouldBe("no /v1/models");
    }

    [Fact]
    public async Task Default_Catches_Throwing_Runtime_And_Reports_NetworkError()
    {
        // A plugin that throws (rather than returning NetworkError)
        // shouldn't surface as a 500 from the host endpoint — the default
        // implementation translates the exception into a clean
        // credential-health signal.
        IAgentRuntime runtime = new FakeRuntime(
            null,
            throwOnFetch: () => new InvalidOperationException("kaboom"));

        var result = await runtime.ValidateCredentialAsync("sk-anything", TestContext.Current.CancellationToken);

        result.Valid.ShouldBeFalse();
        result.Status.ShouldBe(CredentialValidationStatus.NetworkError);
        result.ErrorMessage.ShouldBe("kaboom");
    }

    [Fact]
    public async Task Default_Propagates_Cooperative_Cancellation()
    {
        // OperationCanceledException MUST escape so callers can distinguish
        // "cancelled" from "transport error". The host endpoint does not
        // record cancellations against the credential-health row.
        IAgentRuntime runtime = new FakeRuntime(
            null,
            throwOnFetch: () => new OperationCanceledException());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => runtime.ValidateCredentialAsync("sk-anything", cts.Token));
    }

    private sealed class FakeRuntime : IAgentRuntime
    {
        private readonly FetchLiveModelsResult? _fetchResult;
        private readonly Func<Exception>? _throwOnFetch;

        public FakeRuntime(FetchLiveModelsResult? fetchResult, Func<Exception>? throwOnFetch = null)
        {
            _fetchResult = fetchResult;
            _throwOnFetch = throwOnFetch;
        }

        public string Id => "fake";
        public string DisplayName => "Fake";
        public string Kind => "fake-tool";
        public AgentRuntimeCredentialSchema CredentialSchema => new(AgentRuntimeCredentialKind.ApiKey);
        public string CredentialSecretName => "fake-key";
        public string CredentialEnvVar => "FAKE_API_KEY";
        public IReadOnlyList<ModelDescriptor> DefaultModels => Array.Empty<ModelDescriptor>();
        public string DefaultImage => "fake-image:latest";

        public IReadOnlyList<ProbeStep> GetProbeSteps(AgentRuntimeInstallConfig config, string credential)
            => Array.Empty<ProbeStep>();

        public Task<FetchLiveModelsResult> FetchLiveModelsAsync(
            string credential, CancellationToken cancellationToken = default)
        {
            if (_throwOnFetch is not null)
            {
                throw _throwOnFetch();
            }
            return Task.FromResult(_fetchResult!);
        }

        public bool IsCredentialFormatAccepted(string credential, CredentialDispatchPath dispatchPath) => true;
    }
}