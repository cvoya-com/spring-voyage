/**
 * /settings/packages — moved from `/packages` (#864 / SET-packages).
 *
 * Both routes render the same component until `DEL-packages-top`
 * removes the legacy `/packages` source. A pure re-export keeps the
 * two routes in lockstep without duplicating logic.
 */

export { default } from "@/app/packages/page";
