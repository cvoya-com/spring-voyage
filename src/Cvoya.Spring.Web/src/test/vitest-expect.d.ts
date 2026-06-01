// In vitest 4 the Assertion<T> interface lives in @vitest/expect (not vitest
// itself). @testing-library/jest-dom only augments `declare module 'vitest'`,
// so the matchers become invisible to tsc when imported via the re-export
// path. Augmenting @vitest/expect directly makes tsc see them again.
import type { TestingLibraryMatchers } from "@testing-library/jest-dom/matchers";

declare module "@vitest/expect" {
  interface Assertion<T = unknown>
    extends TestingLibraryMatchers<unknown, T> {}
  interface AsymmetricMatchersContaining
    extends TestingLibraryMatchers<unknown, unknown> {}
}
