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

Multi-tenancy, OAuth/SSO, platform operations, and billing are not "future work"
in this sense — they are commercial extensions developed in the private
repository against the OSS extension seams.
