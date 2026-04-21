/**
 * /settings/agent-runtimes — moved from `/admin/agent-runtimes` (#865 / SET-agent-runtimes).
 *
 * Both routes render the same component until `DEL-admin-top`
 * removes the legacy `/admin/agent-runtimes` source. A pure
 * re-export keeps the two routes in lockstep without duplicating
 * logic — the legacy module still owns the `"use client"` directive
 * and the `../admin-shared` imports.
 */

export { default } from "@/app/admin/agent-runtimes/page";
