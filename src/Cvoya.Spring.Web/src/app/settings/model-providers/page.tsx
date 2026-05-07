/**
 * /settings/model-providers — ADR-0038. Replaces the legacy
 * `/settings/agent-runtimes` route. The implementation lives at
 * `@/components/admin/model-providers-page`; a pure re-export keeps
 * this route thin so the shared component owns the `"use client"`
 * directive and the data fetching.
 */

export { default } from "@/components/admin/model-providers-page";
