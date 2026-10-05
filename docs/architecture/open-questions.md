# Open questions

> **[Architecture index](README.md)** · Related: [Decision records](../decisions/README.md)

Design questions that are **not yet decided**. A question that has been decided
moves to a [decision record](../decisions/README.md); a capability that is
deferred but the architecture accommodates is listed under Future work.

---

## Open

| Question | Where it bites |
|----------|----------------|
| **Actor-state schema evolution** | Versioned serialization for actor-state shape changes across deploys |
| **Initiative policy granularity** | Is `max_level` enough (each level implies a capability set), or are explicit per-capability flags needed? See [Units & agents](units-and-agents.md) |
| **Activity-event stream separation** | Whether to split high-frequency execution events (`TokenDelta`, `ToolCall`) from lower-frequency activity events into two streams. See [Observability](observability.md) |
| **Context-assembly strategy** | Whether prompt Layer 3 stays minimal (agent pulls context on demand) or is pre-assembled richer. See [Units & agents](units-and-agents.md) |
| **Thread ↔ runtime-session association** | A runtime with native session resume keeps its own session id, unlinked from the platform `ThreadId`. Whether to link them is undecided |
| **"Any/one" role-delivery mode** | A role selector delivers to **all** fulfillers ([ADR-0070](../decisions/0070-subjects-agents-humans-teams.md) §9.2). Whether a single-fulfiller mode (claim / on-call / load-balancing semantics) is needed, and its claim protocol, is undecided — it would land in the interactions record's frame ([#3249](https://github.com/cvoya-com/spring-voyage/issues/3249)) |

## Future work

The architecture accommodates these; the interfaces and extension points exist,
the implementations do not.

- **Cognitive backbone** — an optional observer agent that replaces the default
  memory, cognition, and expertise-tracking implementations with cognitive
  equivalents (memory that accumulates, expertise that evolves, pattern
  recognition in the initiative loop). The platform is fully functional without
  it.
- **Expertise marketplace** — metered cross-unit (or cross-deployment) expertise
  access with billing and SLAs, built on the directory and routing fabric.
- **Dynamic agent & unit creation** — agents and units created at runtime to
  meet emerging load, gated by initiative budgets and an `agent.spawn`
  permission.
- **Cross-organisation federation** — multiple Spring Voyage deployments
  federating expertise directories across trust boundaries.
- **Advanced self-organisation** — units that restructure themselves (splitting,
  merging, adjusting policies) based on workload.
- **Persona-level addressing** — distinct routable aliases per package slot,
  beyond the presentation context carried on membership / role-assignment
  relations. Evidence-gated per [ADR-0070](../decisions/0070-subjects-agents-humans-teams.md):
  the trigger is repeated operator demand to be *addressed as* distinct
  personas (e.g. external-channel identity separation) that relation-level
  display context cannot express; any design must keep one accountable
  identity in the audit trail.
- **Multi-parent (DAG) teams** — team topology is a tree with a single parent
  ([ADR-0070](../decisions/0070-subjects-agents-humans-teams.md) §4). The
  revisit trigger is a concrete matrix-organization deployment where two teams
  with mirrored membership demonstrably fail — e.g. policy or resource scoping
  that must be single-homed across two parents. Membership stays M:N either
  way; only the team-parent relation would widen.

Multi-tenancy, OAuth/SSO, platform operations, and billing are not "future work"
in this sense — they are commercial extensions developed in the private
repository against the OSS extension seams.
