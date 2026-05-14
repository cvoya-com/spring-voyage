import { defineConfig, devices } from "@playwright/test";

/**
 * Browser-driven E2E suite for the Spring Voyage portal.
 *
 * Distinct from `src/Cvoya.Spring.Web/e2e/dashboard-smoke.spec.ts`, which boots
 * `next start` against an unreachable API so it can assert hydration without
 * a backend. This suite is the opposite contract: it assumes a *live* local
 * stack (API + Worker + Postgres + Ollama, optionally Caddy) is already running
 * at `PLAYWRIGHT_BASE_URL` (default `http://localhost`) and exercises the
 * portal UI end-to-end against real data.
 *
 * Project pools mirror the shell suite at `tests/e2e/`:
 *   - `fast`   — no LLM call, just CRUD/UI plumbing. Default.
 *   - `llm`    — exercises a real Ollama turn end-to-end. Requires
 *                `LLM_BASE_URL` (or default http://localhost:11434).
 *   - `killer` — the v0.1 "killer use case" flows from the E2 plan
 *                (template wizard → connector binding → engagement).
 *                Bundled separately because they're long and dataful.
 *
 * No `webServer` here — the local stack is operator-managed. The test runner
 * exits immediately if the base URL is unreachable rather than booting one
 * itself; that keeps lifecycle ownership with the human/operator and prevents
 * port collisions with the parallel shell-based suite.
 */
const BASE_URL = process.env.PLAYWRIGHT_BASE_URL ?? "http://localhost";
const FAST_GLOB = "specs/fast/**/*.spec.ts";
const LLM_GLOB = "specs/llm/**/*.spec.ts";
const KILLER_GLOB = "specs/killer/**/*.spec.ts";

export default defineConfig({
  testDir: ".",
  fullyParallel: false, // many specs touch shared tenant state (units list, settings)
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 1 : 1,
  reporter: process.env.CI
    ? [
        ["github"],
        ["list"],
        ["html", { outputFolder: "playwright-report", open: "never" }],
      ]
    : [["list"], ["html", { outputFolder: "playwright-report", open: "never" }]],
  outputDir: "test-results",
  // Long timeouts: validation steps can pull container images, Ollama cold-start
  // takes seconds, and the engagement portal waits on agent responses.
  timeout: 120_000,
  expect: {
    timeout: 15_000,
  },
  use: {
    baseURL: BASE_URL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
    // Most pages render skeletons before data; give them a moment.
    actionTimeout: 20_000,
    navigationTimeout: 30_000,
    // Forwarded to the API helper in fixtures/api.ts — see that file for the
    // resolution order (SPRING_API_URL > PLAYWRIGHT_BASE_URL > localhost).
    extraHTTPHeaders: process.env.SPRING_API_TOKEN
      ? { Authorization: `Bearer ${process.env.SPRING_API_TOKEN}` }
      : undefined,
  },
  projects: [
    {
      name: "fast",
      testMatch: FAST_GLOB,
      use: { ...devices["Desktop Chrome"] },
    },
    {
      name: "llm",
      testMatch: LLM_GLOB,
      use: { ...devices["Desktop Chrome"] },
    },
    {
      name: "killer",
      testMatch: KILLER_GLOB,
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
