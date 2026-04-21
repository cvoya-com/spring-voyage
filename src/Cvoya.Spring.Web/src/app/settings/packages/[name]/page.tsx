/**
 * /settings/packages/[name] — moved from `/packages/[name]` (#864 / SET-packages).
 *
 * Both routes render the same component until `DEL-packages-top`
 * removes the legacy `/packages/[name]` source. A pure re-export
 * keeps the two routes in lockstep without duplicating logic. The
 * server-component page still awaits `params` in the legacy module;
 * re-exporting the default preserves that contract.
 */

export { default } from "@/app/packages/[name]/page";
