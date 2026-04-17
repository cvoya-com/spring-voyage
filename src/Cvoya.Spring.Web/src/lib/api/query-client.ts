import { QueryClient } from "@tanstack/react-query";

/**
 * Factory for the singleton `QueryClient` used across the portal.
 *
 * Defaults are tuned for a live-ops dashboard that pairs REST polling
 * with an SSE activity stream (#437):
 *
 * - `staleTime: 30s` — most screens re-render immediately when the
 *   stream patches the cache, so the REST refetch window can be
 *   generous. The three "polling" sites that previously used
 *   `setInterval(10s)` are now stream-driven; 30s is a fallback for
 *   cold caches or when the stream is temporarily disconnected.
 * - `refetchOnWindowFocus: false` — focus-driven refetches double up
 *   with the stream and cause jitter. Consumers that need "refresh on
 *   return" ask for it explicitly.
 * - `retry: 1` — one retry for flakes; don't hammer a dead API.
 */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        refetchOnWindowFocus: false,
        retry: 1,
      },
      mutations: {
        retry: 0,
      },
    },
  });
}
