# Skills

A **skill** is an authored capability — a package-shipped markdown prompt fragment (with an optional companion tool-requirements file) that an operator equips on a unit or an agent. When the platform assembles the agent's prompt at every message turn, the equipped skills' bodies land inside one of the four assembly layers, extending the agent's behaviour without changing its container image or its user-authored instructions.

Skills are deliberately distinct from [tools](tools.md). A tool is the runtime-invocation surface an agent calls; a skill is authored prose plus an optional list of tools the prose depends on. One skill can name many tools as `RequiredTools`, and a tool can reach an agent through several skills or none at all.

## Shape on disk

A skill lives inside a package at `<package-root>/skills/<skill-name>/`:

```
<package-root>/
└── skills/
    └── triage-and-assign/
        ├── package.yaml          # apiVersion: spring.voyage/v1, kind: Skill
        ├── triage-and-assign.md  # the prompt body (markdown)
        └── triage-and-assign.tools.json   # optional — required-tool declarations
```

`package.yaml` carries the skill's name and a short description. The `.md` file is the **prompt body**. The optional `.tools.json` file declares the tools the skill expects to be available at runtime; the package install pipeline validates these declarations against the registered tool registries and rejects bundles whose required tools no registry exposes (see `Cvoya.Spring.Core.Skills.ISkillBundleValidator`). A prompt-only skill (no `.tools.json`) is valid — its `RequiredTools` list is simply empty.

The recursive layout is documented in detail in [Packages — Skills inside the recursive layout](packages.md#skills-inside-the-recursive-layout).

## The equip model

A package only **ships** a skill. To make it visible to a particular subject, an operator **equips** it on a unit or an agent. Equipping is a per-subject store write keyed by the subject's actor id:

| Subject | Store interface | State-store key prefix | Prompt layer the skill body feeds |
|---|---|---|---|
| Unit | `IUnitSkillBundleStore` | `Unit:SkillBundles:` | Layer 2 — Unit context |
| Agent | `IAgentSkillBundleStore` | `Agent:SkillBundles:` | Layer 4 — Agent instructions |

Both stores are JSON-document state-store records — a single per-subject blob carrying the resolved bundle list — and both share the same mutation surface (`SetAsync`, `AddAsync`, `RemoveAsync`, `DeleteAsync`). Each mutation re-resolves the supplied `SkillBundleReference` values through `ISkillBundleResolver` so the persisted record always carries the freshest prompt body and required-tools snapshot. The OSS resolver reads from the on-disk package tree; the private cloud repo swaps in a tenant-scoped resolver without touching call sites.

A unit binds a package's skill at the tenant level through `tenant_skill_bundle_bindings` (one row per `(tenant, package, skill)` with an `enabled` flag) before any per-subject equip can resolve — the tenant-filtering resolver short-circuits when the binding is absent or disabled. The binding is a tenant-wide gate; the per-subject store is the operator equip decision.

### Why two stores, not one

Unit-equipped and agent-equipped skills land in different prompt-assembly layers — Layer 2 for unit, Layer 4 for agent — and the two have different inheritance semantics (see below). Keeping them in two stores prevents callers from accidentally conflating "I'm reading the unit's bundles" with "I'm reading the agent's bundles"; the type signature draws the line.

## Prompt-assembly placement

The platform assembles a four-layer prompt at every message turn (see [`docs/architecture/agents.md`](../architecture/agents.md#prompt-assembly--platform-tools)). The two skill-equip surfaces land in two of those layers:

| Layer | Source | Content | Equipped skills land here |
|---|---|---|---|
| 1. Platform | System-provided | Tool descriptions, safety constraints | (no skills) |
| 2. Unit context | Actor at activation | Policies, peer directory, skill prompts | **Unit-equipped skills** |
| 3. Thread context | Per invocation | Prior messages, checkpoints, partial results | (no skills) |
| 4. Agent instructions | User-defined | Role-specific guidance, personality | **Agent-equipped skills** |

Each layer renders its bundles inside an `### Skill Bundles` sub-section, with one `#### <PackageName>/<SkillName>` heading per bundle followed by the prompt body and an optional `Required tools:` list. Declaration order in the store is the render order in the prompt.

### Member-agent inheritance

A leaf agent that participates as a member of a unit sees **both** sets of bundles: the unit's via Layer 2 and its own via Layer 4 — no explicit inheritance table is involved. The platform reaches into the appropriate store at message-turn time:

- The agent actor's prompt-assembly context reads from `IAgentSkillBundleStore` keyed by its own actor id (Layer 4).
- The same path resolves the unit context for member agents and reads from `IUnitSkillBundleStore` for the parent unit (Layer 2).

Operators thinking about "what does this agent see at runtime" can reason about the two surfaces independently: equipping a skill on the unit affects every member's Layer 2 in one move; equipping on the agent extends only that agent's Layer 4.

## Web API

Per-subject equip surface (introduced in [#2360](https://github.com/cvoya-com/spring-voyage/issues/2360)):

| Verb | Route | Body | Returns |
|---|---|---|---|
| `GET` | `/api/v1/tenant/units/{id}/skills` | — | `EquippedSkillsResponse` |
| `POST` | `/api/v1/tenant/units/{id}/skills` | `{ packageName, skillName }` | `EquippedSkillsResponse` |
| `DELETE` | `/api/v1/tenant/units/{id}/skills/{packageName}/{skillName}` | — | `EquippedSkillsResponse` |
| `GET` | `/api/v1/tenant/agents/{id}/skills` | — | `EquippedSkillsResponse` |
| `POST` | `/api/v1/tenant/agents/{id}/skills` | `{ packageName, skillName }` | `EquippedSkillsResponse` |
| `DELETE` | `/api/v1/tenant/agents/{id}/skills/{packageName}/{skillName}` | — | `EquippedSkillsResponse` |

`POST` is idempotent on the `(packageName, skillName)` pair — equipping a skill that is already equipped re-resolves the entry in place (refreshing its prompt + required-tools snapshot) but does not duplicate or reorder it. `DELETE` is a no-op when the bundle is not currently equipped. Both mutations return the new effective list so callers can render the post-write state without a follow-up `GET`.

Each response entry is an `EquippedSkillEntry`:

```json
{
  "packageName": "spring-voyage/software-engineering",
  "skillName": "triage-and-assign",
  "promptSummary": "## Triage & Assignment",
  "requiredTools": [
    { "name": "platform.assign_to_agent", "description": "assign work", "optional": false }
  ]
}
```

`promptSummary` is the first non-empty line of the resolved prompt (capped at 200 characters) — informative without dragging the full body through a listing surface. Callers that need the full body fetch the bundle directly.

## Addressing and versioning

Skill addressing is `<package>/<skill>`. No `@<version>` qualifier, no aliases. The package install pipeline does not persist a version column today, so versioned addressing without persistence would be performative. When the tenant reinstalls a package with new content, every equipped subject's next prompt assembly picks up the new body — there is no operator-side pinning. The tradeoff is deliberate; revisiting it requires persisting versions on the binding rows.

## Where the equip data lives

| Concern | Storage | Granularity |
|---|---|---|
| Per-subject equip | `IUnit/IAgentSkillBundleStore` — JSON state-store doc keyed by subject id | One JSON record per `(tenant, subject)` carrying the resolved bundle list |
| Tenant-level package gate | `tenant_skill_bundle_bindings` EF table | One row per `(tenant, package, skill)` with an `enabled` flag |
| Authored body | Filesystem (OSS) or blob storage (cloud) under `<package-root>/skills/<name>/` | One markdown file + optional `.tools.json` per skill |

The two layers compose: the binding row decides whether the tenant has access to a given `(package, skill)`; the per-subject store decides which subjects have equipped it. Removing the binding causes the resolver to refuse subsequent equip attempts but does not retroactively unequip subjects that have already persisted a snapshot — the next mutation on those subjects will surface the failure.

## What skills are not

- **Skills are not tools.** A skill is authored prose plus optional tool requirements. A tool is the runtime-invocation surface. See [Tools](tools.md).
- **Skills are not unit policies.** A policy is a structured JSON document that gates runtime behaviour. A skill is prose injected into the prompt.
- **Skills are not memberships.** Equipping a skill on a unit does not move agents around; it just changes what the unit's Layer 2 contains for every dispatch on that unit. See [`docs/concepts/units-vs-agents.md`](units-vs-agents.md) for the structural side.
