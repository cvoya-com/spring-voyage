# Test-Suite Integrity Audit

> **Point-in-time audit (2026-05-28), [#2890](https://github.com/cvoya-com/spring-voyage/issues/2890).**
> Hunts tests that assert *mocked values* instead of *real behaviour* — "false
> greens." A test that passes whether or not the product code is correct is
> worse than no test. This document is the catalogue; the worst self-contained
> offenders were strengthened in the audit PR, and the coverage gaps were filed
> as issues (linked below).

## Why this audit

Two v0.1-blocking bugs shipped **green** because their tests encoded the very
assumption the bug violated:

- **[#2887](https://github.com/cvoya-com/spring-voyage/issues/2887)** (multi-party engagements collapse to per-recipient threads) — the only multi-recipient-create test mocked `sendMessage` to return the **same** `threadId` for both POSTs, then asserted the second call echoed it. It proved the client loops and echoes the stub; it never exercised the backend producing one shared N-party thread.
- **[#2888](https://github.com/cvoya-com/spring-voyage/issues/2888)** (operator shown read-only on their own thread) — the test mocked `useCurrentUser().id` **equal** to the participant id (the single-Hat happy path); it never modelled "same operator, different Hat," which is exactly the defect.

Both were masked by the same anti-pattern: **a test whose mock setup is precisely the condition under which the bug cannot occur.**

## The falsifiability check

For every suspect test: **"Would this fail if the behaviour it claims to test were broken (deleted or inverted) in the product?"** If deleting/inverting the SUT leaves the test green, it isn't testing that behaviour. A test that uses mocks is fine *if it still asserts real product logic* — the bar is falsifiability, not mock-presence.

## Anti-pattern taxonomy

| Tag | Meaning |
|-----|---------|
| `mock-validates-mock` | Asserts a value the stub was configured to return. |
| `mocking-behavior-under-test` | Stubs the exact function/endpoint whose logic the test claims to verify. |
| `tautological-assertion` | Holds by construction; passes if the impl were deleted/inverted (incl. re-implementing the SUT predicate in the test). |
| `bug-encoding-fixture` | Single-Hat / single-recipient / 2-party fixture that structurally cannot exhibit a defect that lives in the N>1 case. |
| `over-mocking-no-integration` | Mocks so much the nominal integration never runs, with no integration/e2e behind it. |
| `missing-negative-edge` | No assertion for the failure path / empty set / duplicate / cross-identity. |

## Disposition summary

| Disposition | Meaning |
|-------------|---------|
| ✅ **fixed (PR)** | Strengthened in the #2890 audit PR. |
| 🔵 **fixed elsewhere** | Resolved by another PR. |
| ⛓️ **coupled** | Rewrite must land *with* an open product fix (would otherwise go red or assert not-yet-built behaviour). |
| 📋 **filed** | Coverage gap filed as its own issue. |
| 📝 **documented** | Low-severity / acceptable as-is; recorded so it isn't re-flagged. |

The headline result is also a **balanced** one: the resolver/registry/service layer
is genuinely well-tested (see [Confirmed clean](#confirmed-clean-spot-checked)).
The false-greens cluster at **endpoint/contract/composition seams** and the
**engagement portal components**, plus a few real **coverage holes** on the exact
#2887/#2888 seams.

---

## Findings

### Portal — engagement / identity (priority surface)

| Test | Anti-pattern | Falsifiability verdict | Disposition |
|------|--------------|------------------------|-------------|
| `src/Cvoya.Spring.Web/src/components/engagement/engagement-detail.test.tsx` | `bug-encoding-fixture` | The pre-fix suite mocked `/me.id` == the participant id and the observer as an unrelated id; the "same operator, different Hat" case was never exercised. | 🔵 **[#2895](https://github.com/cvoya-com/spring-voyage/pull/2895)** — gates on the full bound-Hat set (`useCallerHumans()`) and adds a falsifiable multi-Hat suite incl. the non-vacuous negative ("observe banner when no bound Hat is a participant"). |
| `…/engagement/new-engagement-form.test.tsx:235` | `mock-validates-mock` | Stub returns `thread-7` for both POSTs; the test asserts the 2nd call echoed `thread-7`. The asserted value *is* the stub's seed — a real per-recipient-thread split (#2887) cannot surface. The product 1:M loop (`new-engagement-form.tsx:129-167`) is still present. | ⛓️ **coupled to [#2887](https://github.com/cvoya-com/spring-voyage/issues/2887)** — see the rewrite spec on that issue. |
| `…/engagement/engagement-list.tsx:116-121,697-701` | `bug-encoding-fixture` (product latent bug) | `userIsParticipant` gates on the lone `/me.id` (no `useCallerHumans()`) and the list **filters** by it — the #2888 root cause in a sibling component #2895 didn't touch. | 📋 **[#2899](https://github.com/cvoya-com/spring-voyage/issues/2899)** |
| `…/app/settings/skills/page.test.tsx:43` | `missing-negative-edge` | "groups skills by registry" asserted both group containers exist and all names render *somewhere* — never **which** skill lands under **which** group; a `slack.post`-under-`builtin` regression stayed green. | ✅ **fixed (PR)** — assertions scoped with `within(...)` per group + cross-filing negatives. |
| `…/components/activity-feed.test.tsx:27` | `tautological-assertion` | Asserts a static heading string with no input dependence. | 📝 **documented** — trivial; the file's other two tests (empty state, source formatting) are honest. |

### Backend — messaging / threading / routing / delivery (priority surface)

| Test | Anti-pattern | Falsifiability verdict | Disposition |
|------|--------------|------------------------|-------------|
| `tests/unit/Cvoya.Spring.Dapr.Tests/Prompts/InboundEnvelopeBuilderTests.cs` (gap) | `over-mocking-no-integration` | `InboundEnvelopeResolver.ResolveRecipientsAsync` (`src/Cvoya.Spring.Dapr/Prompts/InboundEnvelopeBuilder.cs:193-219`) — the live #2887 seam that derives the envelope `to` set from the thread's participants minus the sender — had **zero** tests; only the pure `Render` formatter was covered. A regression that stopped subtracting the sender, or collapsed to the per-hop recipient, would ship green. | ✅ **fixed (PR)** — new `InboundEnvelopeResolverTests`: N-party subtraction, no-thread fallback, sender-only fallback, unresolvable-thread fallback. |
| `…/Messaging/MessageDeliveryServiceTests.cs:172,193-195` | `mock-validates-mock` | `delivered.ThreadId.ShouldBe(_threadRegistry.GetOrCreateAsync({caller,target}))` recomputes the expected id from the **same in-test fake** the SUT used. The companion `ShouldNotBe(inbound.ThreadId)` *is* falsifiable (catches the #2596 regression it is named for), and the recompute still verifies the SUT passed the right participant set. | 📝 **documented** — low severity; the sound relative assertion carries the test. |
| `…/Messaging/MessagingToolHandlersTests.cs:140-144,259-261` | `mock-validates-mock` | Same shape: the `delivered1==delivered2` / multicast-`!=` pair is genuinely falsifiable; the follow-on fake-recompute is redundant. | 📝 **documented** — low severity. |
| `…/Skills/SvMemoryHistoryRegistryTests.cs:138-167` | `mocking-behavior-under-test` | Stubs `IThreadRegistry`, so the thing that matters for #2887 — that `history_with([recipients])` resolves to the **same** thread `send([recipients])` wrote — is never checked (both sides would have to use the real registry). | ⛓️ **coupled to [#2887](https://github.com/cvoya-com/spring-voyage/issues/2887)** — cross-path agreement test noted there. |

### Backend — identity / Hat / permissions / auth (priority surface)

| Test | Anti-pattern | Falsifiability verdict | Disposition |
|------|--------------|------------------------|-------------|
| `tests/unit/Cvoya.Spring.Host.Api.Tests/Contract/AuthContractTests.cs:97` | `tautological-assertion` | `GetCurrentUser_MatchesContract` asserts only that `/me` matches the OpenAPI **schema shape** — never the values or the `Id`↔`TenantUserId` relationship the portal relies on. The #2888 locus is exactly "which identity does `/me` surface?" | 📋 **[#2900](https://github.com/cvoya-com/spring-voyage/issues/2900)** |
| `…/Endpoints/UnitPolicyEndpointsAuthorizationTests.cs:91,116` (+ `UnitHumans`/`UnitTeamMembership` siblings) | `mocking-behavior-under-test`, `bug-encoding-fixture` | `IPermissionService` is a bare stub returning a canned grant; the real `PermissionService` never runs, so the "tenant isolation" claimed in the comments is asserted by stub config. The single-operator fixture can never produce the `Viewer` downgrade the threshold tests assume. | 📋 **[#2900](https://github.com/cvoya-com/spring-voyage/issues/2900)** |

### Backend — Host.Api endpoints / Core / remaining Dapr

Overwhelmingly clean: endpoint tests run a real `WebApplicationFactory` + real
EF + the real auth/permission pipeline, mocking only Dapr-layer collaborators
(the correct use of mocks). The one exception:

| Test | Anti-pattern | Falsifiability verdict | Disposition |
|------|--------------|------------------------|-------------|
| `…/AnalyticsEndpointsTests.cs:36,92` | `mock-validates-mock` | Stubs `AnalyticsQueryService` to return literal rollup values, then asserts those literals back through a 1:1 projection. | 📝 **documented** — redundant, not a hole: the aggregation is covered against real EF by `AnalyticsQueryServiceTests`. (Could seed real events like `CostEndpointsTests` does.) |

### CLI

| Test | Anti-pattern | Falsifiability verdict | Disposition |
|------|--------------|------------------------|-------------|
| `tests/unit/Cvoya.Spring.Cli.Tests/Commands/EngagementCommandTests.cs:340,387` | `tautological-assertion` | Both tests **re-implement** the error filter inline (`mirrors EngagementCommand.CreateErrorsCommand`) and run it over canned events; the product predicate never executes, so a broken filter stays green. | ✅ **fixed (PR)** — extracted `EngagementCommand.IsErrorEvent`; the tests now assert the real predicate (plus a non-match edge). |
| `SkillsCommand.ApplySetAsync` / coordinate parsers (gap) | `over-mocking-no-integration` | `spring (agent\|unit) skills set` diff/apply (`SkillsCommand.cs:271-314`) has zero behavioural coverage — parse-only tests + wrapper tests, but the composition is untested. | 📋 **[#2902](https://github.com/cvoya-com/spring-voyage/issues/2902)** |

### Connectors + Integration suite

| Test | Anti-pattern | Falsifiability verdict | Disposition |
|------|--------------|------------------------|-------------|
| `tests/unit/Cvoya.Spring.Connector.Slack.Tests/SlackPersonaBuilderTests.cs:26-67` | `mock-validates-mock` | Three tests stub the resolver to return "Bob"/"Research"/"Morgan" and assert that exact name back; the builder forwards `address.ToString()` identically for every scheme, so they are one path asserted thrice. | ✅ **fixed (PR)** — collapsed to one `[Theory]` across schemes that asserts the **argument** forwarded to the resolver (`Received(participant.ToString())`), not just the echoed name; the real icon-determinism test was kept. |
| `tests/integration/…/GitHubWebhookFlowTests.cs:27-120` | `bug-encoding-fixture`, `over-mocking-no-integration` | The only GitHub-inbound "integration" test builds the message by hand with a payload shape (`EventType`/`Action`) that doesn't match the real handler's output (`source`/`intent`/`action`); the real translation is never exercised. | 📋 **[#2901](https://github.com/cvoya-com/spring-voyage/issues/2901)** |
| `…/Connector.GitHub.Tests/…/GitHubWebhookHandlerThreadIdTests.cs` | (coupling, not weak today) | Bakes the `(connector://github, unit://…)` pair into the participant set. Faithful to current behaviour, but if [#2889](https://github.com/cvoya-com/spring-voyage/issues/2889) concludes a connector should **not** be a thread participant, these invert. | ⛓️ **coupled to [#2889](https://github.com/cvoya-com/spring-voyage/issues/2889)** — noted there. |

---

## Confirmed clean (spot-checked)

Recorded so the audit's coverage is legible and these aren't re-flagged. Each
was checked against its SUT and found genuinely falsifiable:

- **Identity/permission resolvers** — `TenantUserHumanResolver` (real DB, distinct `hatA`/`hatB`, received-as/originated-as tie-breaks, unbound-Hat negative), `PermissionService` (the #2858 FK walk against a real DB, `DidNotReceive` short-circuit proofs, fail-closed), `HumanIdentityResolver`, `OssPackageHumanResolutionPolicy`.
- **Thread registry / writer** — `EfThreadRegistry` (3-party canonicalisation, order-invariance, dedupe, tenant isolation), `EfMessageWriter` (idempotency, forward-only `last_activity_at`, display-name snapshot).
- **Routing/delivery** — `MessageRouter` persistence-ordering (real EF rows; #2859 permission-before-persist), `MessageDeliveryService` observer fan-out + distinct-hop tests.
- **Core platform** — `A2AExecutionDispatcher`, `BudgetEnforcer` (real EF + threshold math), `ArtefactAutoStartGate`, `CostAggregation`, `SystemEndpoints` plaintext-leak guard.
- **Connectors** — real-HMAC `SlackSignatureValidatorTests` / `WebhookSignatureValidatorTests` (tamper/stale/empty-secret rejection), `GitHubEventFilterTests`, `GitHubConnectorWebhookFilterIntegrationTests` (real connector→handler→filter→validator), `SlackEventDispatcherTests` (3-party fan-out against real EF stores), `LabelRoutingRoundtripSubscriberTests`.
- **CLI** — `CliResolverTests`/`RefResolverTests` (bare-Guid short-circuit asserts no HTTP call; ambiguous-match throws), `ArtefactValidationWaitLoopTests`, `SlackInstallCommandTests` (real in-process servers, targeted rollback).
- **Portal** — `message-composer` (real multi-Hat from-selector path), `conversation-view`, the unit messages tabs + thread switcher (#2885), `lib/api/queries` (`useConnectorBindings` bulk-vs-fan-out regression guard), `use-activity-stream`, `thread/role`.

## Coverage gaps → filed issues

| # | Type | Gap |
|---|------|-----|
| [#2899](https://github.com/cvoya-com/spring-voyage/issues/2899) | Bug | `engagement-list` multi-Hat blind spot (sibling of #2888). |
| [#2900](https://github.com/cvoya-com/spring-voyage/issues/2900) | Task | Backend integration coverage: multi-Hat `/me` value + real `PermissionService` wired to unit endpoints (needs a 2nd Hat in the Host.Api fixture). |
| [#2901](https://github.com/cvoya-com/spring-voyage/issues/2901) | Task | Connector inbound→translation→routing end-to-end integration test (GitHub + Slack). |
| [#2902](https://github.com/cvoya-com/spring-voyage/issues/2902) | Task | CLI `SkillsCommand.ApplySetAsync` diff/apply + coordinate-parser coverage. |

Items coupled to open product work (rewrites that must land *with* the fix):
[#2887](https://github.com/cvoya-com/spring-voyage/issues/2887) (the
`new-engagement-form` 1:M test + the `SvMemoryHistoryRegistry` cross-path test)
and [#2889](https://github.com/cvoya-com/spring-voyage/issues/2889) (the
connector-as-participant test coupling).

## Per-project policy

Weak tests were **rewritten or replaced** — never propped up with product-code
shims. Where a unit test must mock a seam, the audit checked for an integration
test behind it; where one was missing it was added (`InboundEnvelopeResolver`)
or filed (#2900/#2901).
