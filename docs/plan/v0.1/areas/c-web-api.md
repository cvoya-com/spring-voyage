# Area C: Public Web API + OpenAPI contract

**Status:** Planning session pending. Splits into C1 (early audit) and C2 (target shape + freeze).

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
