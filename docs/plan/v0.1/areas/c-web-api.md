# Area C: Public Web API + OpenAPI contract

**Status:** ✅ **Done.** Umbrella [#1216](https://github.com/cvoya-com/spring-voyage/issues/1216) closed. All C1 pieces shipped: C1.1 (reference doc), C1.2a (three-role authz model), C1.2b (URL restructure into `/api/v1/{platform,tenant}/...`), C1.2c (connector split — `provision` for `PlatformOperator`, `bind` for `TenantOperator`), C1.2d (tenant CRUD endpoint), C1.3 (semantic contract tests), and C1.4 (versioning policy). C2 (freeze + publish) was deferred and is tracked as a separate backlog item.

Self-onboarding flow (Google identity → bootstrap a new tenant) is hosted-platform-only and lives at [`cvoya-com/spring#825`](https://github.com/cvoya-com/spring/issues/825).

Original planning notes preserved below.

---

## Scope (provisional)

- **C1 (early, parallel):** audit current public surface, document existing reality, OpenAPI spec for what's there now.
- **C2 (after D / F architecturally settled):** define v0.1 target shape, freeze the contract.

## Dependencies

- C1 depends on: pre-work.
- C2 depends on: D, F.
- E1 (CLI) depends on: C2.

## Open questions

- What is "public" today vs what should be public?
- Where does the OpenAPI source of truth live (code-first vs spec-first)?
- What's the deprecation/versioning policy from v0.1 onward?
- Web Portal continuity: any current portal-only endpoints to either expose or replace?
- How do we test the contract (consumer-driven, schema-locked, golden files)?

## Notes

Hosted-service-foundation lens applies strongly here — the API is the hosted contract. An `openapi.json` file already exists at `src/Cvoya.Spring.Host.Api/openapi.json`; C1 confirms whether it's authoritative or generated.
