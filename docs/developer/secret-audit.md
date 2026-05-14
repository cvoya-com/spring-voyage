# Secret Audit Logging: DI Decoration Pattern

`ISecretResolver` and `ISecretRegistry` (defined in `Cvoya.Spring.Core.Secrets`) are the primary extension points for audit logging, RBAC, metrics, and redaction on the secrets pipeline. The OSS core ships reference implementations and documents the decoration shape so downstream consumers — most importantly the private cloud — can layer behavior cleanly via dependency injection.

This page covers:

- **When** the decorator runs (resolve vs rotate vs list).
- **What** the decorator can observe (call shape, tenant context, resolution details).
- **What the decorator MUST NOT do** (mutate values, log plaintext).
- **How to register** a decorator (manual `Replace` pattern, no third-party packages).

The rotation primitives introduced by #201 and the audit-decoration documentation from #202 are designed together: rotations are exactly the event an audit decorator wants to record, and the `SecretRotation` return value exposes enough for a complete event without any private registry state.

---

## DI Decoration Pattern

The OSS core has **no external DI helper** (no Scrutor, no Autofac). The extension point is a manual `Replace` on the container, documented here and enforced by the contract tests in `SecretResolverDecorationTests`.

### Resolver decoration

```csharp
// Composition root (typically Program.cs)
builder.Services.AddCvoyaSpringDapr(builder.Configuration);

// Register the concrete resolver so the decorator can resolve it
// through DI (the abstraction is already bound to ComposedSecretResolver
// via TryAddScoped).
builder.Services.AddScoped<ComposedSecretResolver>();

// Replace the ISecretResolver binding with the decorator.
builder.Services.Replace(ServiceDescriptor.Scoped<ISecretResolver>(sp =>
    new AuditingSecretResolver(
        inner: sp.GetRequiredService<ComposedSecretResolver>(),
        auditLog: sp.GetRequiredService<IAuditLog>(),
        tenantContext: sp.GetRequiredService<ITenantContext>())));
```

### Registry decoration (rotation audit)

Rotation operations flow through `ISecretRegistry.RotateAsync`, not the resolver. Wrap the registry the same way:

```csharp
builder.Services.AddScoped<EfSecretRegistry>();
builder.Services.Replace(ServiceDescriptor.Scoped<ISecretRegistry>(sp =>
    new AuditingSecretRegistry(
        inner: sp.GetRequiredService<EfSecretRegistry>(),
        auditLog: sp.GetRequiredService<IAuditLog>())));
```

The decorator implements all members of `ISecretRegistry`, forwarding to the inner registry. The `RotateAsync` override is where the audit event fires:

```csharp
public async Task<SecretRotation> RotateAsync(
    SecretRef @ref,
    string newStoreKey,
    SecretOrigin newOrigin,
    Func<string, CancellationToken, Task>? deletePreviousStoreKeyAsync,
    CancellationToken ct)
{
    var rotation = await _inner.RotateAsync(@ref, newStoreKey, newOrigin, deletePreviousStoreKeyAsync, ct);

    _auditLog.RecordRotation(new RotationAuditEvent(
        tenantId: _tenantContext.CurrentTenantId,
        @ref: rotation.Ref,
        fromVersion: rotation.FromVersion,
        toVersion: rotation.ToVersion,
        originChanged: rotation.PreviousPointer.Origin != rotation.NewPointer.Origin,
        previousSlotReclaimed: rotation.PreviousStoreKeyDeleted,
        at: DateTimeOffset.UtcNow));

    return rotation;
}
```

Note we record **AFTER** the inner call — a failed rotation (pre-flight throw, EF-level conflict) never produces a misleading audit row.

### Idempotency: re-running `AddCvoyaSpringDapr`

`AddCvoyaSpringDapr` registers `ISecretResolver` and `ISecretRegistry` with `TryAddScoped`, so a second invocation of the extension method is a no-op for the bindings that already resolve to the decorator. Hosts that compose multiple extension modules (or test harnesses that re-run registration) are safe.

The OSS contract test `SecretResolverDecorationTests.BaselineReRegistration_ViaTryAdd_DoesNotOverwriteDecorator` locks this in — any future change that drops the `TryAdd*` variant will fail CI.

---

## What the decorator observes

### On `ISecretResolver.ResolveWithPathAsync`

Every resolve gives the decorator:

- The requested `SecretRef` (scope, owner id, name).
- The tenant in effect — resolvable from `ITenantContext`. OSS filters every registry query by the current tenant regardless.
- The `SecretResolution` returned by the inner resolver:
  - `Value` — the plaintext (use only for the decision to log `found`/`notFound`; never log the value itself).
  - `Path` — `Direct`, `InheritedFromTenant`, or `NotFound`. Inheritance events are worth an explicit audit signal.
  - `EffectiveRef` — the ref whose registry row was actually read (differs from the requested ref on inheritance).
  - `Version` — the `(int?)` current version from the registry. `null` for legacy rows predating #201, otherwise the integer version served by this resolve.

### On `ISecretRegistry.RotateAsync`

Every rotation gives the decorator a `SecretRotation`:

- `Ref` — the structural reference that was rotated.
- `FromVersion` / `ToVersion` — the version transition. Audit layers usually log both.
- `PreviousPointer` / `NewPointer` — pointer transition. The `Origin` fields will differ when a rotation flipped origins (platform-owned ↔ external reference).
- `PreviousStoreKeyDeleted` — whether the old backing slot was reclaimed. External-reference rotations always have this `false`; platform-owned rotations with a registered delete delegate always have this `true`.

### Rotation delete policy

The OSS core applies an **immediate-delete** policy for platform-owned rotations. Once the registry row points at the new store key, no in-flight reader can reach the old slot, so retaining it only leaks plaintext. Callers that already hold the old plaintext in memory are unaffected. External-reference rotations never touch the backing store — the customer owns that slot.

Auditors interested in a retention window (preserve last N versions for N days) should read the multi-version coexistence follow-up issue filed alongside #201; the retained-versions surface is explicitly NOT in wave 5 A4.

---

## What decorators MUST NOT do

1. **Never mutate the inner return value.** `SecretResolution` and `SecretRotation` are records; treat them as immutable payloads the caller contract depends on. Wrap for audit; don't remap.
2. **Never log plaintext.** `SecretResolution.Value` is private-by-contract. Log only `EffectiveRef`, `Path`, `Version`, and boolean outcome fields.
3. **Never swallow exceptions.** Log them, rethrow. Audit-on-error is a valid pattern; absorbing the error breaks the caller contract.
4. **Never introduce side effects on list/lookup paths.** `ListAsync`, `LookupAsync`, and `LookupWithVersionAsync` must remain cheap — audit writes on every metadata read will dominate cost budgets in practice.

## Best practices

- **Log at `Info` for writes (`Create`, `Rotate`, `Delete`); log at `Debug` for reads.** A production audit sink usually filters reads down to a sampled / rate-limited subset; writes are retained at full fidelity.
- **Include the resolve path in audit events.** `Direct` vs `InheritedFromTenant` is signal. A surge of inheritance fall-throughs can flag a unit that lost its scoped secret.
- **Record the version served.** Post-rotation, the private cloud's audit dashboard can correlate "which version each consumer saw" with the rotation event timestamps.
- **Attach correlation ids.** The OSS core doesn't prescribe a correlation model; the private cloud layers request ids via middleware and the decorator captures them from `HttpContext` / `ActivitySource`.

---

## References

- `src/Cvoya.Spring.Core/Secrets/ISecretResolver.cs` — decoration pattern XML doc.
- `src/Cvoya.Spring.Core/Secrets/ISecretRegistry.cs` — registry decoration + rotation.
- `src/Cvoya.Spring.Core/Secrets/SecretResolution.cs` — observable shape.
- `src/Cvoya.Spring.Core/Secrets/SecretRotation.cs` — rotation event shape.
- `tests/unit/Cvoya.Spring.Dapr.Tests/Secrets/SecretResolverDecorationTests.cs` — contract lock-in.
- `docs/developer/secret-store.md` — storage / encryption / per-tenant components.
