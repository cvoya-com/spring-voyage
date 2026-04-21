/**
 * /settings/system-configuration — moved from `/system/configuration`
 * (#866 / SET-system-config).
 *
 * Post-`DEL-system-top` (#875) the implementation lives at
 * `@/components/admin/system-configuration-page`. A pure re-export
 * keeps this route thin — the shared component owns the
 * `"use client"` directive and the data fetching.
 */

export { default } from "@/components/admin/system-configuration-page";
