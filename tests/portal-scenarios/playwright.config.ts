import { defineConfig, devices } from "@playwright/test";

/**
 * Narrative scenario suite for the Spring Voyage portal.
 *
 * Lighter-weight sibling of `tests/e2e-portal/` (which carries fixtures,
 * helpers, and three project pools). Each spec here is a single user
 * journey written inline — no shared fixtures, no auto-cleanup tracker.
 *
 * Like `tests/e2e-portal/` it assumes a live local stack at
 * `PLAYWRIGHT_BASE_URL` (default `http://localhost`) and exits if the
 * base URL is unreachable rather than booting one itself.
 */
const BASE_URL = process.env.PLAYWRIGHT_BASE_URL ?? "http://localhost";

export default defineConfig({
  testDir: "scenarios",
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: 1,
  reporter: process.env.CI
    ? [["github"], ["list"], ["html", { outputFolder: "playwright-report", open: "never" }]]
    : [["list"], ["html", { outputFolder: "playwright-report", open: "never" }]],
  outputDir: "test-results",
  timeout: 120_000,
  expect: { timeout: 15_000 },
  use: {
    baseURL: BASE_URL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
    actionTimeout: 20_000,
    navigationTimeout: 30_000,
    extraHTTPHeaders: process.env.SPRING_API_TOKEN
      ? { Authorization: `Bearer ${process.env.SPRING_API_TOKEN}` }
      : undefined,
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
