// Tiny helpers for responsive-pass assertions in @testing-library/react
// tests. JSDOM does not render layout, so we cannot assert that a grid
// actually collapses — what we *can* assert is:
//
//   1. The rendered class list carries the mobile-first utility we
//      expect (e.g. `flex-col` with a `sm:flex-row` escape hatch).
//   2. No descendant exceeds the viewport width via a literal
//      `w-[NNNpx]` or `min-w-[NNNpx]` utility that would force the page
//      past the device width at 375px.
//
// Playwright is the right tool for true pixel-level overflow checks;
// the helpers below exist so the vitest suite can catch regressions
// without a browser.

/**
 * Check that the rendered tree does not carry any utility class that
 * pins a descendant to a pixel width wider than the target viewport.
 * We walk the DOM and flag `w-[NNNpx]` / `min-w-[NNNpx]` / inline
 * `style="width: NNNpx"` that exceeds the target — these are the
 * responsive-footgun shapes we've tripped on in the past.
 *
 * Missing values (zero, unitless, other units) are ignored — the check
 * is intentionally narrow; it only fires on a clear-cut violation.
 */
export function assertNoOverflowingPixelWidths(
  root: HTMLElement,
  targetWidthPx: number,
): void {
  const offenders: string[] = [];
  const elements = root.querySelectorAll<HTMLElement>("*");
  for (const el of elements) {
    for (const token of Array.from(el.classList)) {
      const m = token.match(/^(?:min-)?w-\[(\d+)px\]$/);
      if (m) {
        const px = Number(m[1]);
        if (px > targetWidthPx) {
          offenders.push(`<${el.tagName.toLowerCase()} class="${token}">`);
        }
      }
    }
    const inlineWidth = (el.style.width || "").trim();
    const mInline = inlineWidth.match(/^(\d+(?:\.\d+)?)px$/);
    if (mInline && Number(mInline[1]) > targetWidthPx) {
      offenders.push(
        `<${el.tagName.toLowerCase()} style="width: ${inlineWidth}">`,
      );
    }
  }
  if (offenders.length > 0) {
    throw new Error(
      `Found ${offenders.length} element(s) with a fixed pixel width > ${targetWidthPx}px:\n  ${offenders.join(
        "\n  ",
      )}`,
    );
  }
}

/**
 * Assert that a container classifies as responsive: it must either
 * carry a mobile-first stacking utility (`flex-col`, `grid-cols-1`) or
 * an overflow escape hatch (`overflow-x-auto`, `overflow-auto`,
 * `flex-wrap`). This is the single "did the author think about mobile
 * here?" gate the portal wants to enforce on page-level headers.
 */
export function assertResponsiveContainer(el: HTMLElement): void {
  const classes = el.className;
  const hasStackingUtility =
    /\bflex-col\b/.test(classes) ||
    /\bgrid-cols-1\b/.test(classes) ||
    /\bflex-wrap\b/.test(classes) ||
    /\boverflow-x-auto\b/.test(classes) ||
    /\boverflow-auto\b/.test(classes) ||
    /\bblock\b/.test(classes);
  if (!hasStackingUtility) {
    throw new Error(
      `Expected container to carry a responsive utility (flex-col / grid-cols-1 / flex-wrap / overflow-x-auto / block). Got: "${classes}".`,
    );
  }
}
