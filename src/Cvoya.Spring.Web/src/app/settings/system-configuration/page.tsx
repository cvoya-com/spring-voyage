/**
 * /settings/system-configuration — moved from `/system/configuration`
 * (#866 / SET-system-config).
 *
 * Both routes render the same component until `DEL-system-top`
 * removes the legacy `/system/configuration` source. A pure
 * re-export keeps the two routes in lockstep without duplicating
 * logic — the legacy module still owns the `"use client"` directive.
 */

export { default } from "@/app/system/configuration/page";
