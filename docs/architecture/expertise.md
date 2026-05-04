# Expertise

> **[Architecture Index](README.md)** | Related: [Units](units.md), [Agents](agents.md), [Orchestration](orchestration.md), [Agent Runtime](agent-runtime.md)

This document covers expertise profiles, the unit directory, recursive aggregation, directory search, and seeding from YAML. Expertise is how the platform answers "who can do this?" — it powers peer discovery (`discoverPeers` platform tool), boundary projection, and the `directory/search` meta-skill.

---

## Expertise Profiles

Each agent has an expertise profile — seeded from config, optionally evolved through a cognitive backbone (see [Open Questions — Future Work](open-questions.md)):

```yaml
ExpertiseProfile:
  agent: agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7
  domains:
    - name: python/fastapi
      level: expert
      source: config                 # or "cognitive" if evolved
    - name: react/nextjs
      level: novice
      source: observed               # emerged from experience
```

Default implementation: profiles stay at seeded values. With a cognitive backbone: domains level up, new domains emerge, stale expertise decays.

---

## Directory

The directory is a **property of the unit** — each unit maintains its members' expertise profiles. Directories compose recursively through the unit hierarchy. The root unit aggregates all.

### Recursive Expertise Aggregation

A unit's **effective expertise** is the union of:

1. The unit's own configured domains (from `UnitActor.SetOwnExpertiseAsync`) — used when a unit advertises a synthesised capability that isn't owned by any single member.
2. Every descendant's effective expertise, composed recursively down through sub-units to each leaf agent.

Each contributed capability is preserved with:

| Field | Meaning |
|-------|---------|
| `Domain` | The name, description, and optional level (`beginner | intermediate | advanced | expert`). |
| `Origin` | Address of the contributor (`agent:<id>` for leaves, `unit:<id>` when a nested unit advertises its own domain). |
| `Path` | Ordered addresses from the aggregating unit down to `Origin`. Length − 1 is the depth. |

The origin chain lets peer-lookup callers tell **where** a capability came from so they can route work to the leaf, and lets permission checks (#414) decide whether the requester is allowed to traverse into that origin. The path is what the boundary layer (#413) consumes when it decides to project, filter, or synthesize.

**De-duplication.** When the same `(domain-name, origin)` pair is reachable through multiple DAG paths (e.g. a shared sub-unit), the aggregator collapses duplicates. If levels disagree on the collapsed pair, the stronger level wins — "closest to the root" never silently downgrades an `expert` contribution.

**Walk bound.** The recursive walk is bounded by the same depth cap (`64`) that `UnitActor.AddMemberAsync` uses for membership cycle detection. Exceeding the bound surfaces as `ExpertiseAggregationException` so a misconfigured parent cycle is reported, not silently looped. A back-edge onto the aggregating unit is likewise rejected — a DAG that converges benignly on a non-root node is fine, but one that closes back on the root is a hard error.

**Caching.** Aggregated snapshots are cached per unit via `IExpertiseAggregator`. Membership changes (add/remove member, assign/unassign agent) and expertise edits (agent or unit own expertise) call `IExpertiseAggregator.InvalidateAsync`. Invalidation walks **up** — for an agent origin, through the agent's unit memberships; for a unit origin, by resolving every unit whose members list contains the child — evicting the target plus every ancestor so the next aggregate read recomputes. There's no TTL: aggregated expertise is small, writes are rare, and invalidation is precise.

**Extension points.** The aggregator is a DI service (`TryAddSingleton<IExpertiseAggregator, ExpertiseAggregator>`). A cloud host can pre-register a decorator to layer tenant-scoped caches or audit logging. A future **boundary projection / filtering** layer (#413) will plug in as a filter over the aggregator's output rather than altering the walk itself, so the basic recursive composition keeps shipping unchanged while opacity rules evolve.

**Typed-contract entries are skill-callable (#359).** An `ExpertiseDomain` whose `InputSchemaJson` is non-null has declared a structured request shape — the platform treats it as a **skill** and surfaces it through `IExpertiseSkillCatalog` / `ExpertiseSkillRegistry` with the catalog-addressable name `expertise/{slug}`. Consultative-only entries (no schema) stay message-only. External callers see only boundary-projected entries; agent-level entries inside a unit are skill-callable only by callers already inside the boundary. All skill invocations flow through `ISkillInvoker` and, by default, back onto `IMessageRouter` so the boundary / permission / policy / activity chain runs end-to-end. See [Agent Runtime — Skill registries](agent-runtime.md#4a-skill-registries) for the full projection.

**Wire surface.**

- `GET /api/v1/agents/{id}/expertise` · `PUT /api/v1/agents/{id}/expertise` — per-agent profile.
- `GET /api/v1/units/{id}/expertise/own` · `PUT /api/v1/units/{id}/expertise/own` — unit-level own expertise (no aggregation).
- `GET /api/v1/units/{id}/expertise` — effective / recursive-aggregated expertise.
- `POST /api/v1/directory/search` — lexical / full-text search (#542). Hit payload carries the full owner chain + `projection/{slug}` paths as of #553 so callers see every projecting ancestor, not just the immediate aggregating unit.
- CLI: `spring agent expertise get|set <id>`, `spring unit expertise get|set|aggregated <id>`, plus the tenant-wide browse trio `spring directory list`, `spring directory show <slug>`, and `spring directory search "<query>"` — same shape on every surface for UI/CLI parity (closes #528, #553).

### Directory Search (#542)

Enumerate + exact-lookup leaves the directory unusable when the caller knows only a capability description ("refactor this Python") and not the exact slug. `IExpertiseSearch.SearchAsync` takes an `ExpertiseSearchQuery` (free text + owner / domain / typed-only / pagination filters + boundary view context) and returns a ranked list of `ExpertiseSearchHit` records. Ranking:

1. Exact slug match.
2. Exact tag / domain match.
3. Owner filter hit (caller supplied a concrete address).
4. Text relevance — substring matches on slug, display name, description, owner name.
5. Aggregated-coverage — the entry surfaced via a descendant unit's projection (ranked just below direct matches).

**Boundary.** Outside-the-unit callers see only unit-projected entries; inside callers see the full scope. The default `InMemoryExpertiseSearch` also drops agent-origin hits for external callers as defence in depth, so a misconfigured boundary cannot leak through a search result. Performance target: &lt;200ms on a 1000-entry tenant (validated by `InMemoryExpertiseSearchPerformanceTests`).

**Meta-skill.** `directory/search` is exposed through `ISkillRegistry` (as `DirectorySearchSkillRegistry`) so a planner — or any `ISkillInvoker` consumer — can call it BEFORE any `expertise/*` skill to resolve a capability description into concrete slugs. The output schema (`{ totalCount, limit, offset, hits: [...] }`) is published on the tool definition so callers can validate the response shape at the transport layer. `MessageRouterSkillInvoker` routes `directory/search` calls in-process rather than through the message bus — meta-skills do not target an agent / unit and therefore do not need the router's boundary / permission chain.

**Extension points.** `IExpertiseSearch` is a DI seam (`TryAddSingleton<IExpertiseSearch, InMemoryExpertiseSearch>`). The private cloud repo can register a Postgres-FTS (or embedding-backed) implementation without touching any caller. Issue #542 Step 2 (semantic / embedding search) is tracked as a separate follow-up.

### Seeding from YAML

`AgentDefinition` and `UnitDefinition` YAML can declare an `expertise:` block that the platform auto-applies to actor state on first activation. This closes the gap where declared intent — visible on the definition — was not observable through `GET .../expertise` until the operator pushed the same entries back through `PUT .../expertise` (or the `spring ... expertise set` CLI).

```yaml
# agents/tech-lead.yaml
agent:
  id: tech-lead
  expertise:
    - domain: architecture
      level: expert
    - domain: code-review
      level: expert
```

**Precedence: actor state wins.** The seed is applied only when actor state for the expertise key (`Agent:Expertise` or `Unit:OwnExpertise`) is _unset_. Once an operator has written a value — even an empty list via `PUT .../expertise` with `[]` — the actor is authoritative and subsequent activations do not re-seed from YAML. This preserves runtime edits across process restarts and lets an operator clear seeded expertise without the YAML silently re-adding it on the next reactivation. The alternative ("seed always overwrites on activation") was rejected: it would force operators to re-declare every runtime tweak back into the manifest to survive a restart, turning a one-off configuration touch into a recurring bookkeeping tax.

**Implementation.** `IExpertiseSeedProvider` reads the persisted definition JSON (`AgentDefinitions.Definition` / `UnitDefinitions.Definition`). `AgentActor.OnActivateAsync` and `UnitActor.OnActivateAsync` check the state key with `TryGetStateAsync`; when `HasValue == false` they pass the seed through the same `SetExpertiseAsync` / `SetOwnExpertiseAsync` path an HTTP PUT would take, so a seeded actor is wire-indistinguishable from one that received a PUT with the same payload. Failures in seeding are logged and non-fatal — activation proceeds with empty expertise and the operator can push it manually.

**Key spelling.** The YAML authoring key is `domain:` (matches every shipped package manifest). The seed provider also accepts `name:` so a dump from `GET /api/v1/agents/{id}/expertise` (which emits `name`) can be round-tripped back into a definition file without renaming.

---

## See Also

- [Units](units.md) — unit entity model; unit directory as a property of the unit
- [Agents](agents.md) — agent model; `discoverPeers` platform tool
- [Orchestration](orchestration.md) — boundary configuration; how opacity/projection/synthesis rules filter expertise
- [Agent Runtime](agent-runtime.md) — skill registries; how `expertise/{slug}` skills are invoked
- [Open Questions](open-questions.md) — cognitive backbone for expertise evolution (future work)
