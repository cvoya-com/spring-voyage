# Portal data layer

Every page-level data fetch in the portal goes through **TanStack
Query** over the typed **openapi-fetch** client. Two things matter
here:

- A single transport (`./client.ts` — `api.*`) so request/response
  types stay in lockstep with the OpenAPI contract.
- A single cache (`./query-provider.tsx` — mounted at the app root in
  `src/app/layout.tsx`) so that stream-driven invalidations from
  `useActivityStream` actually hit the UI.

## The pattern

```tsx
import { useUnit } from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";

export function UnitHeader({ id }: { id: string }) {
  const { data: unit, isPending, error } = useUnit(id);
  // Subscribe to SSE so the cache patches as events arrive.
  useActivityStream();

  if (isPending) return <Skeleton />;
  if (error) return <ErrorBanner message={error.message} />;
  if (!unit) return null;
  return <h1>{unit.displayName}</h1>;
}
```

That's it — no `useEffect`, no `setInterval`, no manual loading
state. The query key comes from `query-keys.ts`; SSE events from
`/api/stream/activity` invalidate the matching slice and the query
refetches automatically.

## Files

| File | Purpose |
| --- | --- |
| `client.ts` | Thin typed wrappers over `openapi-fetch`. One method per API op. |
| `queries.ts` | `useQuery` / `useMutation` hooks over `client.ts`. Add a hook here whenever you need to call the API from a React tree. |
| `query-keys.ts` | The single source of truth for cache keys. Never invent ad-hoc keys inline. |
| `query-client.ts` | Singleton `QueryClient` factory with portal-wide defaults (`staleTime`, `refetchOnWindowFocus`, `retry`). |
| `query-provider.tsx` | Client-boundary wrapper that mounts `QueryClientProvider`. Imported by `src/app/layout.tsx`. |

## Streaming ↔ cache wiring

`src/lib/stream/use-activity-stream.ts` opens the SSE stream at
`/api/stream/activity` and, for every event, calls
`queryClient.invalidateQueries({ queryKey: ... })` for the slices
affected by the event's source (see `queryKeysAffectedBySource` in
`query-keys.ts`). This is the mechanism that replaces the old
`setInterval` polling loops — consumers just use `useQuery`; the
stream hook takes care of freshness.

When adding a new query:

1. Add an `api.*` method in `client.ts` (if it doesn't already exist).
2. Add a query-key entry under `queryKeys.<surface>.*` in
   `query-keys.ts`.
3. Add a typed `useX` wrapper in `queries.ts` that binds them
   together.
4. If new event types should invalidate the surface, extend
   `queryKeysAffectedBySource`.

## Mutations

Mutations still use the raw `api.*` methods today. After the mutation
resolves, hand-seed the cache with `queryClient.setQueryData(...)`
for the immediate write-through effect, or call
`queryClient.invalidateQueries({ queryKey: queryKeys.<surface>.all })`
to force a refetch.

Consumers that need typed `useMutation` hooks — follow the same
pattern as `queries.ts`. Add on demand; the surface should stay small
until there's a real caller.

## See also

- `docs/design/portal-exploration.md` §8.3 (streaming) + §8.4
  (TanStack) — the design decision.
- `src/app/api/stream/activity/route.ts` — the Next.js SSE route
  handler that proxies the platform stream.
- `src/lib/stream/use-activity-stream.ts` — the client hook that
  consumes it.
