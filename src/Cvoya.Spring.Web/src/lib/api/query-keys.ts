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
  },

  activity: {
    all: ["activity"] as const,
    query: (params?: Record<string, string>) =>
      ["activity", "query", params ?? {}] as const,
  },

  conversations: {
    all: ["conversations"] as const,
    list: (filters?: Record<string, unknown>) =>
      ["conversations", "list", filters ?? {}] as const,
    detail: (id: string) => ["conversations", "detail", id] as const,
    inbox: () => ["conversations", "inbox"] as const,
  },

  tenant: {
    budget: () => ["tenant", "budget"] as const,
  },

  connectors: {
    all: ["connectors"] as const,
    list: () => ["connectors", "list"] as const,
    detail: (slugOrId: string) =>
      ["connectors", "detail", slugOrId] as const,
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
      queryKeys.conversations.all,
      queryKeys.units.detail(source.path),
      queryKeys.units.cost(source.path),
      queryKeys.conversations.all,
    ];
  }
  if (scheme === "agent") {
    return [
      queryKeys.activity.all,
      queryKeys.dashboard.all,
      queryKeys.conversations.all,
      queryKeys.agents.detail(source.path),
      queryKeys.agents.cost(source.path),
      // Lifecycle (#396) rides the same activity SSE — container health
      // transitions surface as `StateChanged` events scoped to the agent.
      // Invalidating here keeps the lifecycle panel fresh without a
      // separate poller.
      queryKeys.agents.deployment(source.path),
      queryKeys.conversations.all,
    ];
  }
  if (scheme === "conversation") {
    return [
      queryKeys.activity.all,
      queryKeys.conversations.all,
      queryKeys.conversations.detail(source.path),
    ];
  }
  if (scheme === "human") {
    return [
      queryKeys.activity.all,
      queryKeys.dashboard.all,
      queryKeys.conversations.all,
      queryKeys.conversations.inbox(),
    ];
  }
  return [
    queryKeys.activity.all,
    queryKeys.dashboard.all,
    queryKeys.conversations.all,
  ];
}
