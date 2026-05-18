// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.Policies;

using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Units;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the OSS-default contract for
/// <see cref="IUnitPolicyEnforcer.EvaluateUnitDirectoryReadAsync"/>: within
/// a single tenant the enforcer returns Allowed unconditionally, because
/// tenant isolation is enforced one layer down by the tenant-scoped EF
/// queries the directory tools issue. Multi-tenant / private-cloud
/// deployments swap in a stricter enforcer; this test exists so a future
/// edit that tightens or loosens the OSS default does so deliberately.
/// </summary>
public class UnitDirectoryReadEnforcementTests
{
    [Fact]
    public async Task DefaultUnitPolicyEnforcer_AllowsAnyDirectoryReadInOss()
    {
        var enforcer = new DefaultUnitPolicyEnforcer(
            new EmptyMembershipRepo(),
            new EmptyPolicyRepo());

        var verdict = await enforcer.EvaluateUnitDirectoryReadAsync(
            callerId: Guid.NewGuid().ToString("N"),
            targetUnitId: Guid.NewGuid(),
            cancellationToken: TestContext.Current.CancellationToken);

        verdict.IsAllowed.ShouldBeTrue();
        verdict.DenyingUnitId.ShouldBeNull();
    }

    private sealed class EmptyMembershipRepo : IUnitMembershipRepository
    {
        public Task<UnitMembership?> GetAsync(Guid unitId, Guid agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<UnitMembership?>(null);

        public Task<UnitMembership?> UpdateRolesAndExpertiseAsync(
            Guid unitId,
            Guid agentId,
            IReadOnlyList<string> roles,
            IReadOnlyList<string> expertise,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<UnitMembership?>(null);

        public Task<IReadOnlyList<UnitMembership>> ListByUnitAsync(Guid unitId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnitMembership>>(Array.Empty<UnitMembership>());

        public Task<IReadOnlyList<UnitMembership>> ListByAgentAsync(Guid agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnitMembership>>(Array.Empty<UnitMembership>());

        public Task<IReadOnlyList<UnitMembership>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnitMembership>>(Array.Empty<UnitMembership>());

        public Task UpsertAsync(UnitMembership membership, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid unitId, Guid agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAllForAgentAsync(Guid agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyPolicyRepo : IUnitPolicyRepository
    {
        public Task<UnitPolicy> GetAsync(Guid unitId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UnitPolicy());

        public Task SetAsync(Guid unitId, UnitPolicy policy, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid unitId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
