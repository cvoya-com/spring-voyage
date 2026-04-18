// Shared accessibility regression harness for the portal. Each top-level
// route has a smoke spec that renders a representative tree and calls
// `expectNoAxeViolations(container)` — any new route added to the
// sidebar should get a matching spec. See `docs/design/portal-exploration.md`
// § 7 and #446 for the bar.
//
// The helper wraps `vitest-axe`'s `configureAxe` with a ruleset tuned
// for Vitest + JSDOM:
//
//  - Disables `color-contrast`. JSDOM does not compute styles (every
//    element reports `rgba(0,0,0,0)`), so the rule false-positives on
//    every run. Contrast is verified manually against the DESIGN.md § 2
//    token set and by the responsive pass (#445) when real styles render.
//  - Restricts the run to WCAG 2.0 / 2.1 AA criteria (the portal's
//    target, per the design doc). Best-practice rules are still advisory
//    and worth fixing, but they do not block the smoke tests.
//  - Keeps every other rule enabled — missing labels, focus order,
//    landmark coverage, etc. are all live.
//
// The `toHaveNoViolations()` matcher is registered in
// `src/test/setup.ts`, so specs only need to await this helper.

import { configureAxe } from "vitest-axe";
// Importing the side-effect entry augments Vitest's `expect` with the
// `toHaveNoViolations()` matcher that `expectNoAxeViolations()` calls.
// Using the side-effect import here (rather than relying on the global
// setup file) means the type augmentation is visible to `tsc` under
// `next build`, which does not compile the setup file.
import "vitest-axe/extend-expect";
import { expect } from "vitest";

const axe = configureAxe({
  // `runOnly` restricts the engine to WCAG-AA rules; violations outside
  // the AA band (e.g. best-practice nudges) are reported as needs-review
  // rather than failing a run. Expand only when the bar rises.
  runOnly: {
    type: "tag",
    values: ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"],
  },
  rules: {
    "color-contrast": { enabled: false },
    // `scrollable-region-focusable` inspects computed overflow; without
    // real layout JSDOM cannot decide whether a region is scrollable
    // and axe reports false positives on every overflow-* utility.
    "scrollable-region-focusable": { enabled: false },
  },
});

/**
 * Shorthand assertion used by every route-level smoke spec. Renders
 * the page with its mocked dependencies, waits for the view to settle,
 * then calls this helper against `container`:
 *
 * ```ts
 * const { container } = render(<DashboardPage />, { wrapper });
 * await screen.findByRole("heading", { name: /dashboard/i });
 * await expectNoAxeViolations(container);
 * ```
 */
export async function expectNoAxeViolations(
  container: Element,
): Promise<void> {
  const results = await axe(container);
  // `toHaveNoViolations` is registered by `vitest-axe/extend-expect`
  // through `src/test/setup.ts`. The runtime type augmentation only
  // kicks in while tests execute, so `tsc` under `next build` doesn't
  // see the new matcher on Vitest's strongly-typed `Assertion<T>`. We
  // cast through `unknown` → our minimal matcher interface so the
  // build still type-checks without erasing the matcher at runtime.
  const matcher = expect(results) as unknown as {
    toHaveNoViolations(): void;
  };
  matcher.toHaveNoViolations();
}

export { axe };
