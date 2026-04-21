/**
 * /settings/agent-runtimes — moved from `/admin/agent-runtimes` (#865 / SET-agent-runtimes).
 *
 * The implementation lives at `@/components/admin/agent-runtimes-page`
 * after `DEL-admin-top` (#876) retired the `/admin/*` routes. A pure
 * re-export keeps this route thin — the shared component owns the
 * `"use client"` directive and the data fetching.
 */

export { default } from "@/components/admin/agent-runtimes-page";
