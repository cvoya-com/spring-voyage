"use client";

// Human detail route (#2266 / #2267).
//
// Mirrors the Explorer's Detail Pane chrome (`<DetailPane>`) but
// skips the left-rail tree because humans don't appear in the
// tenant-tree payload in v0.1 — the platform addresses humans
// directly by Guid (`human:<guid>`) and the dedicated route is the
// natural reachability seam. Cmd-K teleport and unit-membership rows
// from #2270 + #2427 link here via `/humans/<id>`; the activity
// stream's `human:` sources resolve here too.
//
// The page is also reachable from the Explorer via
// `?node=human:<guid>` once the routing seam in `units/page.tsx`
// rewrites to here — see PR body for the migration plan.

import { Suspense, useCallback, useMemo } from "react";
import { Loader2 } from "lucide-react";
import { notFound, useParams, usePathname, useSearchParams } from "next/navigation";

import { DetailPane } from "@/components/units/unit-detail-pane";
import type { HumanNode, HumanTabName, TabName } from "@/components/units/aggregate";
import { tabsFor } from "@/components/units/aggregate";
import { useHuman } from "@/lib/api/queries";

// Side-effect import — registers every Explorer tab (including
// `Human × Overview` from #2267 and the slot-reserved Messages /
// Config placeholders). Keeping the import local to the page means
// the registry is wired the moment a human page mounts without
// pulling the whole Explorer tree apparatus.
import "@/components/units/tabs/register-all";

// #2473: accept both dashed and no-dash UUID forms so the canonical
// /explorer/humans/<id> and legacy /humans/<dashed-uuid> both work.
const UUID_RE =
  /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;

/**
 * Normalise a no-dash (32-char) or dashed (36-char) UUID to the
 * canonical dashed form used by the API.
 */
function normalizeToDashed(id: string): string {
  const clean = id.replace(/-/g, "");
  if (clean.length !== 32) return id; // Unrecognised — pass through.
  return `${clean.slice(0, 8)}-${clean.slice(8, 12)}-${clean.slice(12, 16)}-${clean.slice(16, 20)}-${clean.slice(20)}`;
}

function HumanDetailRoute() {
  const params = useParams<{ id: string }>();
  const rawId = params.id;
  const pathname = usePathname();
  const searchParams = useSearchParams();

  const tab = (searchParams.get("tab") as HumanTabName | null) ?? undefined;
  // #2473: normalise no-dash UUID to dashed form for API calls.
  const humanId = normalizeToDashed(rawId ?? "");

  // Guard against routing-table accidents — Next.js does not enforce
  // the [id] segment shape. A non-UUID slug surfaces as 404 so we
  // never round-trip a malformed value to the API.
  const isValidId = UUID_RE.test(humanId);

  const humanQuery = useHuman(humanId, { enabled: isValidId });

  const writeTab = useCallback(
    (next: TabName) => {
      // DetailPane's onTabChange is shaped against the union TabName
      // (any kind's tab). For the Human route only HumanTabName values
      // are reachable because the strip is built from HUMAN_TABS, but
      // we accept the wider type at the prop boundary so the shared
      // DetailPane stays kind-agnostic.
      const params = new URLSearchParams(searchParams.toString());
      params.set("tab", next);
      const qs = params.toString();
      const target = qs ? `${pathname}?${qs}` : pathname;
      // Same client-owned URL update as the Explorer route — keeps
      // tab clicks off the RSC navigation path so the Detail Pane
      // doesn't flicker on every selection.
      window.history.replaceState(null, "", target);
    },
    [pathname, searchParams],
  );

  // Synthesise a HumanNode from the live human entity. The node
  // carries only the cross-subject identity fields; the Overview
  // body fetches the full record via `useHuman` itself, so the page
  // doesn't duplicate the network call.
  const node = useMemo<HumanNode | null>(() => {
    if (!humanQuery.data) return null;
    return {
      kind: "Human",
      id: humanQuery.data.id,
      // Use the displayName when set; fall back to the username so
      // the header never shows the raw Guid (the Detail Pane has a
      // UUID-shape guard but we side-step it explicitly here so the
      // anchor text reads as a person, not "Human / ID: …").
      name: humanQuery.data.displayName || humanQuery.data.username,
      status: "running",
    };
  }, [humanQuery.data]);

  if (!isValidId) {
    notFound();
  }

  if (humanQuery.isError) {
    return (
      <div
        role="alert"
        data-testid="human-detail-error"
        className="rounded-lg border border-destructive/40 bg-destructive/5 p-4 text-sm text-destructive"
      >
        Failed to load human <code className="font-mono">{humanId}</code>.
      </div>
    );
  }

  if (humanQuery.isLoading || !node) {
    if (!humanQuery.isLoading && humanQuery.data === null) {
      // 404 / not-in-tenant collapse into a clean Not Found page — the
      // dedicated route should not leak an empty header when the human
      // is missing, only the inline body inside the Explorer.
      notFound();
    }
    return (
      <div
        role="status"
        aria-live="polite"
        data-testid="human-detail-loading"
        className="flex h-full min-h-[50vh] items-center justify-center text-sm text-muted-foreground"
      >
        <Loader2 className="mr-2 h-4 w-4 animate-spin" aria-hidden="true" />
        Loading human…
      </div>
    );
  }

  const allTabs = tabsFor("Human");
  const activeTab: HumanTabName =
    tab && (allTabs as readonly HumanTabName[]).includes(tab) ? tab : allTabs[0];

  return (
    <div
      data-testid="human-detail-route"
      className="flex h-[calc(100vh-6rem)] min-h-[480px] flex-col"
    >
      <div className="min-h-0 flex-1">
        <DetailPane
          node={node}
          path={[node]}
          tab={activeTab}
          onTabChange={writeTab}
          // Human pages have no breadcrumb tree to navigate; clicking
          // the (only) breadcrumb stays on the page.
          onSelectNode={() => {}}
        />
      </div>
    </div>
  );
}

export default function HumanDetailPage() {
  // `useSearchParams` requires a Suspense boundary in the App Router.
  return (
    <Suspense
      fallback={
        <div
          role="status"
          aria-live="polite"
          data-testid="human-detail-loading"
          className="flex h-full min-h-[50vh] items-center justify-center text-sm text-muted-foreground"
        >
          <Loader2
            className="mr-2 h-4 w-4 animate-spin"
            aria-hidden="true"
          />
          Loading human…
        </div>
      }
    >
      <HumanDetailRoute />
    </Suspense>
  );
}
