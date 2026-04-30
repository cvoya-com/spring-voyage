# 0031 — Kiota nullable-oneOf / polymorphic-discriminator tracking

**Status:** tracking (upstream issues open)
**Date:** 2026-04-29
**Issues:** #1043

## Context

Spring Voyage uses Microsoft Kiota-generated API clients throughout the .NET platform layer. Two upstream Kiota defects affect how nullable `oneOf` schemas and polymorphic discriminators are handled in generated code.

## Upstream issues

| Issue | Title | Impact |
|---|---|---|
| [microsoft/kiota#6776](https://github.com/microsoft/kiota/issues/6776) | Nullable `oneOf` types generate non-nullable properties | Generated DTOs drop nullability, causing runtime null-reference errors on optional fields |
| [microsoft/kiota#7573](https://github.com/microsoft/kiota/issues/7573) | Polymorphic discriminator mapping not applied when base type is abstract | Deserialization of derived types silently falls back to the base type, losing type-specific fields |

## Current workaround

Until the upstream fixes land and we pin to a fixed Kiota version:

- Avoid relying on discriminator-based polymorphic deserialization in new code; use explicit `switch` on a `kind`/`type` field instead.
- Treat all Kiota-generated optional properties as potentially null even when the generated type says otherwise.

## Resolution trigger

When either upstream issue is closed and a Kiota release that includes the fix is available, open a follow-up task to:

1. Re-pin `Kiota*` packages to the fixed version.
2. Remove local workarounds.
3. Add a regression test covering the previously-broken schema shape.
