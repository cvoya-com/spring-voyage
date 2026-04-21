/**
 * /settings/packages — moved from `/packages` (#864 / SET-packages).
 *
 * Post-`DEL-packages-top` (#874) the implementation lives at
 * `@/components/admin/packages-page`. A pure re-export keeps this
 * route thin.
 */

export { default } from "@/components/admin/packages-page";
