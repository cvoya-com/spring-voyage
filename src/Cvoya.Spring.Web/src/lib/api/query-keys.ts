/**
 * Query-key factory for the portal's TanStack Query cache.
 *
 * Every `useQuery`/`useMutation` in the web project should build its
 * key through one of these functions so the activity-stream hook and
 * `queryClient.invalidateQueries({ queryKey: queryKeys.<surface>.all })`
 * can find the right slices to patch/invalidate. Keys are shaped as
 * tuples: the first element is the surface, further elements narrow
 * the slice.
 *
 * Convention:
 *   - `all` — the whole surface (invalidate everything on that feature)
 *   - `detail(id)` — one entity by id
 *   - `list(params?)` — the indexed list, optionally with params
 */
export const queryKeys = {
  dashboard: {
    all: ["dashboard"] as const,
    summary: () => ["dashboard", "summary"] as const,
    agents: () => ["dashboard", "agents"] as const,
    units: () => ["dashboard", "units"] as const,
    costs: () => ["dashboard", "costs"] as const,
  },

  agents: {
    all: ["agents"] as const,
    list: () => ["agents", "list"] as const,
    detail: (id: string) => ["agents", "detail", id] as const,
    memberships: (id: string) => ["agents", "memberships", id] as const,
    cost: (id: string) => ["agents", "cost", id] as const,
    budget: (id: string) => ["agents", "budget", id] as const,
    clones: (id: string) => ["agents", "clones", id] as const,
    initiativePolicy: (id: string) =>
      ["agents", "initiativePolicy", id] as const,
    initiativeLevel: (id: string) =>
      ["agents", "initiativeLevel", id] as const,
    // Persistent-agent lifecycle (#396). The deployment slice is the
    // PersistentAgentDeploymentResponse for one agent; the logs slice is
    // a per-(id, tail) snapshot. Both invalidate on the matching agent
    // activity key via `queryKeysAffectedBySource`.
    deployment: (id: string) => ["agents", "deployment", id] as const,
    logs: (id: string, tail: number) =>
      ["agents", "logs", id, tail] as const,
    expertise: (id: string) => ["agents", "expertise", id] as const,
    execution: (id: string) => ["agents", "execution", id] as const,
    costTimeseries: (id: string, window: string, bucket: string) =>
      ["agents", "costTimeseries", id, window, bucket] as const,
    costBreakdown: (id: string) => ["agents", "costBreakdown", id] as const,
    /** Persistent cloning policy (PR-PLAT-CLONE-1, #534). */
    cloningPolicy: (id: string) => ["agents", "cloningPolicy", id] as const,
    /** Runtime-status indicator polled by `<RuntimeStatusBadge>` (#2100). */
    runtimeStatus: (id: string) => ["agents", "runtimeStatus", id] as const,
  },

  units: {
    all: ["units"] as const,
    list: () => ["units", "list"] as const,
    detail: (id: string) => ["units", "detail", id] as const,
    fullDetail: (id: string) => ["units", "fullDetail", id] as const,
    readiness: (id: string) => ["units", "readiness", id] as const,
    cost: (id: string) => ["units", "cost", id] as const,
    agents: (id: string) => ["units", "agents", id] as const,
    memberships: (id: string) => ["units", "memberships", id] as const,
    /**
     * Team-role human members (`GET /api/v1/tenant/units/{id}/members/humans`,
     * #2409). One row per (humanId, role) pair. Powers the human cards on
     * the Unit × Members tab (#2270 / #2427).
     */
    humanMembers: (id: string) => ["units", "humanMembers", id] as const,
    /**
     * Sub-unit member rows with their per-membership roles + expertise
     * (`GET /api/v1/tenant/units/{id}/members/units`, #2463). Used to
     * seed the sub-unit edit dialog on the Members tab. Sub-unit display
     * names come from the tenant tree — this query carries only the
     * metadata columns.
     */
    subUnitMembers: (id: string) => ["units", "subUnitMembers", id] as const,
    secrets: (id: string) => ["units", "secrets", id] as const,
    connector: (id: string) => ["units", "connector", id] as const,
    githubConfig: (id: string) => ["units", "githubConfig", id] as const,
    initiativePolicy: (id: string) =>
      ["units", "initiativePolicy", id] as const,
    policy: (id: string) => ["units", "policy", id] as const,
    boundary: (id: string) => ["units", "boundary", id] as const,
    execution: (id: string) => ["units", "execution", id] as const,
    memberHosting: (id: string) => ["units", "memberHosting", id] as const,
    ownExpertise: (id: string) => ["units", "ownExpertise", id] as const,
    aggregatedExpertise: (id: string) =>
      ["units", "aggregatedExpertise", id] as const,
    costTimeseries: (id: string, window: string, bucket: string) =>
      ["units", "costTimeseries", id, window, bucket] as const,
    /** Runtime-status indicator polled by `<RuntimeStatusBadge>` (#2100). */
    runtimeStatus: (id: string) => ["units", "runtimeStatus", id] as const,
    budget: (id: string) => ["units", "budget", id] as const,
    deployment: (id: string) => ["units", "deployment", id] as const,
  },

  directory: {
    all: ["directory"] as const,
    expertise: () => ["directory", "expertise"] as const,
  },

  /**
   * Per-human read-side slices (#2266 / #2267). Mirrors the agents / units
   * shape so the Explorer's Human page (#2267) and the Portal Wave B
   * briefs (#2268 Messages, #2269 Config, #2270 + #2427 Unit × Members)
   * can dedupe identical reads through one cache slot.
   */
  humans: {
    all: ["humans"] as const,
    detail: (id: string) => ["humans", "detail", id] as const,
    // ADR-0047 §§ 2, 14: connector-identity keys relocate onto the
    // `tenantUsers` surface below. The per-Human identity slot is
    // removed with no shim per v0.1's freezing-release policy.
  },

  /**
   * TenantUser surface (ADR-0047 §§ 2, 14). The `/settings/user-identity`
   * page reads through these slices and the wizard's auth-choice
   * sub-step invalidates `identities(id)` when an OAuth completion
   * lands a new GitHub `username` on the calling tenant user.
   */
  tenantUsers: {
    all: ["tenantUsers"] as const,
    detail: (id: string) => ["tenantUsers", "detail", id] as const,
    identities: (id: string) =>
      ["tenantUsers", "identities", id] as const,
    /**
     * The calling caller's bound-Human ("Hat") set from
     * `GET /api/v1/tenant/users/me/humans` (ADR-0062 §§ 3, 5). Backs the
     * `<HumanFromSelector>`, per-Hat inbox chip, and "Claim this
     * Human" affordance. Invalidate after a successful binding patch
     * so every dependent surface re-reads in one pass.
     */
    callerHumans: () => ["tenantUsers", "callerHumans"] as const,
  },

  /**
   * Tenant-scoped secrets (#615) used by the user-identity page's
   * orphan-secret listing (ADR-0047 §5: the wizard's pre-mint flow
   * writes secrets at `binding/<bindingId>/github/pat`; if the operator
   * abandons the wizard the secret persists for cleanup).
   */
  tenantSecrets: {
    all: ["tenantSecrets"] as const,
    list: () => ["tenantSecrets", "list"] as const,
  },

  activity: {
    all: ["activity"] as const,
    query: (params?: Record<string, string>) =>
      ["activity", "query", params ?? {}] as const,
  },

  /**
   * Analytics rollups (#448 / #457). The CLI `spring analytics
   * {costs,throughput,waits}` and the portal's `/analytics/*` pages
   * share these keys. Slice shape:
   *   - `throughput(source?, from, to)` — key per scope + window.
   *   - `waits(source?, from, to)` — same.
   *   - `costs` is served by `queryKeys.dashboard.costs()` already; the
   *     Analytics Costs page fetches through that hook and adds a filter
   *     layer client-side so the underlying cache remains shared with
   *     the dashboard header.
   */
  analytics: {
    all: ["analytics"] as const,
    throughput: (params: {
      source?: string;
      from: string;
      to: string;
    }) => ["analytics", "throughput", params] as const,
    waits: (params: {
      source?: string;
      from: string;
      to: string;
    }) => ["analytics", "waits", params] as const,
  },

  threads: {
    all: ["threads"] as const,
    list: (filters?: Record<string, unknown>) =>
      ["threads", "list", filters ?? {}] as const,
    detail: (id: string) => ["threads", "detail", id] as const,
    inbox: () => ["threads", "inbox"] as const,
  },

  /**
   * Tenant-wide read-only observation surface (#2787). Backs the
   * /conversations portal page + `spring conversations list` CLI verb.
   * Distinct from `threads.*` because the wire endpoints differ
   * (`/api/v1/tenant/observation/threads`) and the role gate differs
   * (`TenantObserver` vs. `TenantUser`). Per-thread streaming is shared
   * with the participant-scoped views via `useThreadStream`.
   */
  conversations: {
    all: ["conversations"] as const,
    list: (filters?: Record<string, unknown>) =>
      ["conversations", "list", filters ?? {}] as const,
    detail: (id: string) => ["conversations", "detail", id] as const,
  },

  tenant: {
    all: ["tenant"] as const,
    budget: () => ["tenant", "budget"] as const,
    /**
     * Per-window tenant cost rollup (PR-R4, #394). Each distinct
     * `(from, to)` window caches independently so the dashboard
     * summary card's today / 7d / 30d tiles don't clobber each
     * other.
     */
    cost: (from: string, to: string) =>
      ["tenant", "cost", from, to] as const,
    /**
     * Tenant cost time-series (V21-tenant-cost-timeseries, #916). Keyed
     * on `(window, bucket)` so the `/budgets` sparkline (30d / 1d) and
     * the forthcoming analytics stacked-area chart (#910) can share the
     * same cache slot without colliding. The key is the source-of-truth
     * grain — two surfaces asking for the same window+bucket dedupe
     * transparently.
     */
    costTimeseries: (window: string, bucket: string) =>
      ["tenant", "costTimeseries", window, bucket] as const,
    /**
     * Tenant tree payload served by `GET /api/v1/tenant/tree`. Consumed
     * by `<UnitExplorer>` — any unit/agent mutation should invalidate
     * this slice so the Explorer re-renders with the new shape.
     */
    tree: () => ["tenant", "tree"] as const,
    /**
     * Tenant-wide persistent cloning policy (PR-PLAT-CLONE-1, #534).
     * Served by `GET /api/v1/tenant/cloning-policy`.
     */
    cloningPolicy: () => ["tenant", "cloningPolicy"] as const,
    /**
     * Client-side tenant-wide policy rollup (#909). Aggregates per-unit
     * `UnitPolicyResponse` values; keyed on "rollup" so the
     * `/policies` page and the Explorer tenant-policies tab share the
     * same cache slot without colliding with the per-unit slices.
     */
    policyRollup: () => ["tenant", "policyRollup"] as const,
  },

  memories: {
    all: ["memories"] as const,
    unit: (id: string) => ["memories", "unit", id] as const,
    agent: (id: string) => ["memories", "agent", id] as const,
  },

  /**
   * Equipped skill bundles (#2360 / #2362). Per-subject slice keyed on
   * (scope, id) so the same id can't collide between unit and agent
   * caches. `available` covers the derived browse list — the union of
   * `kind: Skill` entries across every installed package's
   * {@link PackageDetail.skills} array. We don't have a dedicated
   * "/api/v1/packages/skills" endpoint at v0.1, so the cache slice
   * just memoises the client-side derivation.
   */
  skills: {
    all: ["skills"] as const,
    equipped: (scope: "unit" | "agent", id: string) =>
      ["skills", "equipped", scope, id] as const,
    available: () => ["skills", "available"] as const,
  },

  issues: {
    all: ["issues"] as const,
    unit: (id: string) => ["issues", "unit", id] as const,
    agent: (id: string) => ["issues", "agent", id] as const,
    counts: (subjects: readonly string[]) =>
      ["issues", "counts", ...[...subjects].sort()] as const,
  },

  connectors: {
    all: ["connectors"] as const,
    list: () => ["connectors", "list"] as const,
    detail: (slugOrId: string) =>
      ["connectors", "detail", slugOrId] as const,
    credentialHealth: (slugOrId: string, secretName?: string) =>
      ["connectors", "credentialHealth", slugOrId, secretName ?? null] as const,
    githubInstallations: () =>
      ["connectors", "github", "installations"] as const,
    githubInstallUrl: () =>
      ["connectors", "github", "install-url"] as const,
    // ADR-0061 §1: tenant-scoped binding query. One row per
    // (tenant, connector_slug) — keyed on the slug so the Slack
    // settings panel and any future tenant-scoped connectors share
    // the same cache slice.
    tenantBinding: (slug: string) =>
      ["connectors", "tenant-binding", slug] as const,
  },

  templates: {
    list: () => ["templates", "list"] as const,
    detail: (pkg: string, name: string) =>
      ["templates", "detail", pkg, name] as const,
  },

  packages: {
    all: ["packages"] as const,
    list: () => ["packages", "list"] as const,
    detail: (name: string) => ["packages", "detail", name] as const,
    requiredCredentials: (name: string) =>
      ["packages", "requiredCredentials", name] as const,
  },

  // Install status (ADR-0035 decision 11). Per-install slice keyed by id
  // so polling two concurrent installs doesn't clobber each other's cache.
  installs: {
    all: ["installs"] as const,
    detail: (id: string) => ["installs", "detail", id] as const,
  },

  ollama: {
    models: () => ["ollama", "models"] as const,
  },

  // Tenant-installed model providers (ADR-0038, was: agent runtimes
  // #690) — per-provider cache so switching the provider dropdown
  // doesn't clobber the previous provider's model list.
  modelProviders: {
    all: ["modelProviders"] as const,
    list: () => ["modelProviders", "list"] as const,
    models: (providerId: string) =>
      ["modelProviders", providerId, "models"] as const,
    credentialHealth: (providerId: string, secretName?: string) =>
      ["modelProviders", providerId, "credentialHealth", secretName ?? null] as const,
  },

  // Settings drawer (#451) — drawer panels fetch a small amount of
  // per-panel metadata (version/build hash; signed-in user; token
  // list). Single-tuple keys because each slice is global.
  platform: {
    info: () => ["platform", "info"] as const,
  },

  auth: {
    me: () => ["auth", "me"] as const,
    tokens: () => ["auth", "tokens"] as const,
  },

} as const;

/**
 * Maps an activity event source (e.g. `unit://unit-alpha` or
 * `agent://agent-1`) to the query keys that are likely to become stale
 * on that event. Used by `useActivityStream` (see
 * `src/lib/stream/use-activity-stream.ts`) to patch/invalidate the
 * right cache slices when a new event arrives.
 */
export function queryKeysAffectedBySource(source: {
  scheme: string;
  path: string;
}): readonly (readonly string[])[] {
  const scheme = source.scheme.toLowerCase();
  if (scheme === "unit") {
    return [
      queryKeys.activity.all,
      queryKeys.dashboard.all,
      queryKeys.threads.all,
      queryKeys.units.detail(source.path),
      queryKeys.units.cost(source.path),
      queryKeys.threads.all,
      // #2387: lifecycle transitions change the per-node status surfaced
      // in the tenant tree. Invalidate the tree slice here so the left
      // explorer dots refresh without a manual page reload.
      queryKeys.tenant.tree(),
    ];
  }
  if (scheme === "agent") {
    return [
      queryKeys.activity.all,
      queryKeys.dashboard.all,
      queryKeys.threads.all,
      queryKeys.agents.detail(source.path),
      queryKeys.agents.cost(source.path),
      // Lifecycle (#396) rides the same activity SSE — container health
      // transitions surface as `StateChanged` events scoped to the agent.
      // Invalidating here keeps the lifecycle panel fresh without a
      // separate poller.
      queryKeys.agents.deployment(source.path),
      queryKeys.threads.all,
      // #2387: agent lifecycle transitions roll up into the tenant tree.
      queryKeys.tenant.tree(),
    ];
  }
  if (scheme === "thread") {
    return [
      queryKeys.activity.all,
      queryKeys.threads.all,
      queryKeys.threads.detail(source.path),
    ];
  }
  if (scheme === "human") {
    return [
      queryKeys.activity.all,
      queryKeys.dashboard.all,
      queryKeys.threads.all,
      queryKeys.threads.inbox(),
    ];
  }
  return [
    queryKeys.activity.all,
    queryKeys.dashboard.all,
    queryKeys.threads.all,
  ];
}
