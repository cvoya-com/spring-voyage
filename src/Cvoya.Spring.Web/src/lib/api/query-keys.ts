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
    skills: (id: string) => ["agents", "skills", id] as const,
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
    secrets: (id: string) => ["units", "secrets", id] as const,
    connector: (id: string) => ["units", "connector", id] as const,
    githubConfig: (id: string) => ["units", "githubConfig", id] as const,
    initiativePolicy: (id: string) =>
      ["units", "initiativePolicy", id] as const,
    policy: (id: string) => ["units", "policy", id] as const,
    boundary: (id: string) => ["units", "boundary", id] as const,
    orchestration: (id: string) => ["units", "orchestration", id] as const,
    execution: (id: string) => ["units", "execution", id] as const,
    ownExpertise: (id: string) => ["units", "ownExpertise", id] as const,
    aggregatedExpertise: (id: string) =>
      ["units", "aggregatedExpertise", id] as const,
    costTimeseries: (id: string, window: string, bucket: string) =>
      ["units", "costTimeseries", id, window, bucket] as const,
  },

  directory: {
    all: ["directory"] as const,
    expertise: () => ["directory", "expertise"] as const,
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
  },

  memories: {
    all: ["memories"] as const,
    unit: (id: string) => ["memories", "unit", id] as const,
    agent: (id: string) => ["memories", "agent", id] as const,
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
  },

  skills: {
    catalog: () => ["skills", "catalog"] as const,
  },

  ollama: {
    models: () => ["ollama", "models"] as const,
  },

  // Tenant-installed agent runtimes (#690) — per-runtime cache so
  // switching the runtime dropdown doesn't clobber the previous
  // runtime's model list.
  agentRuntimes: {
    all: ["agentRuntimes"] as const,
    list: () => ["agentRuntimes", "list"] as const,
    models: (runtimeId: string) =>
      ["agentRuntimes", runtimeId, "models"] as const,
    credentialHealth: (runtimeId: string, secretName?: string) =>
      ["agentRuntimes", runtimeId, "credentialHealth", secretName ?? null] as const,
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
