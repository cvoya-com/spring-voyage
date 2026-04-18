import "@testing-library/jest-dom/vitest";
import "vitest-axe/extend-expect";
import * as axeMatchers from "vitest-axe/matchers";
import { expect } from "vitest";

// Register the `toHaveNoViolations()` matcher used by the a11y smoke
// tests in `src/test/a11y.ts`. Added once at setup so individual specs
// don't need to wire it up themselves. See #446 — the portal now treats
// axe-core violations on the smoke routes as regressions, so every new
// route should add a matching spec via `expectNoAxeViolations()`.
expect.extend(axeMatchers);

// `cmdk` observes its list container to animate height, and Radix
// primitives used elsewhere inspect it too — JSDOM doesn't ship
// ResizeObserver so we add a no-op stub. Matches the guidance in
// https://github.com/pacocoursey/cmdk/issues/150.
if (typeof globalThis.ResizeObserver === "undefined") {
  class ResizeObserverStub {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  globalThis.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;
}

// JSDOM doesn't implement Element.prototype.scrollIntoView; cmdk's
// selection logic calls it on every keypress to keep the active item
// visible. Stub as a no-op so the tests exercise filter / selection
// behaviour rather than hit a DOM polyfill hole.
if (typeof Element !== "undefined" && !Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = function scrollIntoViewStub() {};
}
