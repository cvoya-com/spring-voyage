#!/usr/bin/env node
/**
 * Bundle-size budget for the dashboard's client JS payload.
 *
 * Run AFTER `npm run build` (or `next build`); fails CI if any of the
 * configured budgets are exceeded.
 *
 * Why this script and not @next/bundle-analyzer or size-limit?
 *   - `next build` under Turbopack doesn't print per-route First Load JS
 *     totals the way Webpack did, so we can't grep stdout.
 *   - `@next/bundle-analyzer` produces an interactive HTML report — great
 *     for humans, useless as a CI gate without bespoke parsing.
 *   - `size-limit` doesn't grok Next's chunked output without an entry
 *     wrapper per route.
 *   - Computing the on-disk sum of `.next/static/chunks/*.js` (raw and
 *     gzipped) plus the largest single chunk catches the regressions we
 *     actually care about (a fat dependency creeping into the client
 *     bundle, or a single route ballooning).
 *
 * Tighten budgets here as the suite shrinks; relax with explicit
 * justification in the PR description.
 */

import { readdir, readFile, stat } from "node:fs/promises";
import { gzipSync } from "node:zlib";
import path from "node:path";
import process from "node:process";

const KB = 1024;

// Budgets — current measured values plus modest headroom.
//
// Updated 2026-05-17 (#2451) when Human became the fourth Explorer
// subject (#2266 / #2267 — new dedicated `/humans/[id]` route, new
// `HumanOverviewBody`, three reserved tab slots, additional lucide
// icons, new `useHuman` / `useCurrentUser` consumers). The new route
// pulls `register-all` into its own entry, so the per-route bundle
// grew correspondingly:
//   Total uncompressed: ~2866 KB → cap 2950 KB
//   Total gzipped:      ~ 815 KB → cap  850 KB (unchanged; still within)
//   Largest chunk (uncompressed): ~356 KB → cap 450 KB (unchanged)
//
// Only the uncompressed total crossed its prior cap; gzipped and
// largest-chunk are unchanged because the new code is text-heavy
// (icons, comments, type-narrowing) and compresses well.
//
// Headroom on uncompressed is intentionally tight (~3%) — this is a
// foundation PR; the matching Wave B PRs (#2268 Messages, #2269 Config,
// #2270 / #2427 Unit × Members) should reuse the same registry imports
// and not push the cap further. Re-tune only when a substantial new
// surface lands.
//
// Updated 2026-04-30 (#1427) when the analytics surface adopted
// recharts (charts: #910) and @tanstack/react-virtual (data-table: #911):
//   Total uncompressed: ~2185 KB → cap 2800 KB
//   Total gzipped:      ~ 636 KB → cap  850 KB
//   Largest chunk (uncompressed): ~356 KB → cap 450 KB
//
// Both deps were intentional new functionality on the analytics route.
// They are eagerly imported on the analytics pages; lazy-loading does
// not change the on-disk total this script measures (chunks are still
// emitted), so raising the budget is the right control here.
//
// Previous measurements (kept for reference):
//   2026-04-21: total ~1290 KB / gz ~371 KB / largest ~224 KB
// Updated 2026-05-18 (#2480) to account for @tailwindcss/typography plugin
// and the remark/rehype ecosystem (react-markdown + remark-gfm) landing in
// the shared client bundle when ThreadEventRow adopted markdown rendering.
// react-markdown and remark-gfm were already installed; they move from the
// admin-only packages route chunk into the shared chunk now that
// thread-event-row.tsx (used on every conversation surface) imports them.
//   Total uncompressed: ~3070 KB → cap 3150 KB
//   Total gzipped:      ~ 871 KB → cap  900 KB
//   Largest chunk (uncompressed): 356 KB → cap 450 KB (unchanged)
//
// Updated 2026-05-19 (#2544) when the unit Explorer pane's "Create
// sub-unit" button became a "Create member" dropdown — new dropdown
// markup, a `CreateMemberItem` row component, the agent/human create
// wiring, and a few lucide icons. The heavy agent-create form is
// already eagerly bundled via the unit Members tab, so the increase is
// the dropdown code itself and lazy-loading the dialogs would not move
// this on-disk total. Only the uncompressed figure moved; gzipped and
// largest-chunk are unchanged.
//   Total uncompressed: ~3156 KB → cap 3180 KB
//   Total gzipped:      ~ 898 KB → cap  900 KB (unchanged; still within)
//   Largest chunk (uncompressed): 356 KB → cap 450 KB (unchanged)
//
// Updated 2026-05-24 when the system_prompt_mode toggle landed on the
// agent + unit execution panels — new `SystemPromptModeControl`
// component (radio-group + cascade pill + help text) plus the
// per-panel wiring (mode state, PATCH/PUT mutations, clear-override
// affordance) in both `execution-panel.tsx` (agent) and
// `execution-tab.tsx` (unit). No new dependencies; growth is the new
// component's own footprint plus the duplicated wiring across both
// surfaces. Gzipped total measured 900 KB locally / 902 KB in CI
// (environment variance puts headroom at effectively zero); bumping
// to 925 KB gives a real cushion so the next small PR doesn't re-fire
// the same alarm.
//   Total uncompressed: ~3166 KB → cap 3220 KB
//   Total gzipped:      ~ 900 KB → cap  925 KB
//   Largest chunk (uncompressed): 356 KB → cap 450 KB (unchanged)
//
// Updated 2026-05-25 (#2779) when the web-search connector landed its
// portal surface — two new React components (`WebSearchConnectorTab`
// and `WebSearchConnectorWizardStep`) shipped under
// `src/Cvoya.Spring.Connector.WebSearch/web/`, plus the registry
// entry and the typed `api.*WebSearch*` wrappers in `lib/api/client.ts`.
// No new dependencies; growth is the new component code (plain
// React + the shared `@/components/ui/*` primitives already in the
// bundle) and the additional typed surface in the API client.
// Gzipped total went DOWN slightly (chunk shuffling rebalanced
// existing modules) so only the uncompressed cap moves.
//   Total uncompressed: ~3237 KB → cap 3260 KB
//   Total gzipped:      ~ 918 KB → cap  925 KB (unchanged; still within)
//   Largest chunk (uncompressed): 356 KB → cap 450 KB (unchanged)
//
// Updated 2026-05-26 (#2787) when the tenant-wide read-only
// /conversations view landed — a new full-page surface alongside Inbox
// and Engagement that reuses `<ConversationView>` without the composer.
// The increment is the new page itself (`app/conversations/page.tsx`),
// the new `useConversations` / `useConversation` query hooks, and the
// matching navigation + palette entries in `lib/extensions/defaults.tsx`.
// No new dependencies; the shared bundle picks up an additional route
// chunk plus its incremental client code. Gzipped total moved past the
// previous 925 cap so both totals are bumped together.
//   Total uncompressed: ~3355 KB → cap 3400 KB
//   Total gzipped:      ~ 949 KB → cap  975 KB
//   Largest chunk (uncompressed): 356 KB → cap 450 KB (unchanged)
//
// Updated 2026-05-27 (#2867) when the tenant-wide Interactions
// visualisation landed — `/activity/interactions` adds a graph view
// (`@xyflow/react`), a matrix, a brushable recharts timeline, an SSE
// stream consumer, and a detail popover. `@xyflow/react` is the new
// dependency (~115 KB gzipped uncompressed, ~37 KB gzipped on top of
// existing recharts). The new code itself is text-heavy (helpers,
// matrix, filters) and compresses well. The Activity surface also
// gained a layout-with-tabs shell so the events page sits one segment
// deeper. Bump both totals together; largest chunk is unchanged
// because the new graph code lazy-splits naturally.
//   Total uncompressed: ~3599 KB → cap 3650 KB
//   Total gzipped:      ~1040 KB → cap 1075 KB
//   Largest chunk (uncompressed): 360 KB → cap 450 KB (unchanged)
//
// Updated 2026-05-31 (#2968) when the GitHub-connector unit-creation
// wizard gained the App-free "Paste a PAT" path, the Safari-safe OAuth
// server-poll fallback, and the PAT inbound-webhook note, plus the
// `api.getGitHubOAuthResult` client wrapper. No new dependencies; the
// growth is the additional component code (plain React + shared
// `@/components/ui/*` primitives) and the typed API surface. Gzipped
// stayed comfortably within budget; only the uncompressed total crossed
// its prior 3650 cap.
//   Total uncompressed: ~3656 KB → cap 3700 KB
//   Total gzipped:      ~1060 KB → cap 1075 KB (unchanged; still within)
//   Largest chunk (uncompressed): 358 KB → cap 450 KB (unchanged)
const BUDGETS = {
  totalUncompressedKb: 3700,
  totalGzippedKb: 1075,
  maxChunkUncompressedKb: 450,
};

const projectRoot = path.resolve(import.meta.dirname, "..");
const chunksDir = path.join(projectRoot, ".next", "static", "chunks");

async function ensureChunksDir() {
  try {
    const s = await stat(chunksDir);
    if (!s.isDirectory()) {
      throw new Error(`${chunksDir} is not a directory`);
    }
  } catch (err) {
    console.error(
      `bundle-budget: ${chunksDir} not found. Run \`npm run build\` first.`,
    );
    console.error(err instanceof Error ? err.message : err);
    process.exit(2);
  }
}

async function* walkJs(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      yield* walkJs(full);
    } else if (entry.isFile() && entry.name.endsWith(".js")) {
      yield full;
    }
  }
}

async function main() {
  await ensureChunksDir();

  let total = 0;
  let totalGz = 0;
  let largest = { file: "", size: 0 };

  for await (const file of walkJs(chunksDir)) {
    const buf = await readFile(file);
    total += buf.byteLength;
    totalGz += gzipSync(buf).byteLength;
    if (buf.byteLength > largest.size) {
      largest = { file: path.relative(projectRoot, file), size: buf.byteLength };
    }
  }

  const totalKb = Math.round(total / KB);
  const totalGzKb = Math.round(totalGz / KB);
  const largestKb = Math.round(largest.size / KB);

  console.log("bundle-budget report");
  console.log("--------------------");
  console.log(
    `total client JS (uncompressed): ${totalKb} KB / ${BUDGETS.totalUncompressedKb} KB`,
  );
  console.log(
    `total client JS (gzipped):      ${totalGzKb} KB / ${BUDGETS.totalGzippedKb} KB`,
  );
  console.log(
    `largest single chunk:           ${largestKb} KB / ${BUDGETS.maxChunkUncompressedKb} KB (${largest.file})`,
  );

  const failures = [];
  if (totalKb > BUDGETS.totalUncompressedKb) {
    failures.push(
      `total client JS (uncompressed) ${totalKb} KB > ${BUDGETS.totalUncompressedKb} KB budget`,
    );
  }
  if (totalGzKb > BUDGETS.totalGzippedKb) {
    failures.push(
      `total client JS (gzipped) ${totalGzKb} KB > ${BUDGETS.totalGzippedKb} KB budget`,
    );
  }
  if (largestKb > BUDGETS.maxChunkUncompressedKb) {
    failures.push(
      `largest single chunk ${largestKb} KB > ${BUDGETS.maxChunkUncompressedKb} KB budget (${largest.file})`,
    );
  }

  if (failures.length > 0) {
    console.error("");
    console.error("bundle-budget FAILED:");
    for (const f of failures) console.error(`  - ${f}`);
    console.error("");
    console.error(
      "Investigate with `npx next-bundle-analyzer` or by inspecting `.next/static/chunks/`.",
    );
    console.error(
      "If the increase is intentional, raise the budget in scripts/check-bundle-size.mjs and call it out in the PR description.",
    );
    process.exit(1);
  }

  console.log("");
  console.log("bundle-budget OK");
}

await main();
