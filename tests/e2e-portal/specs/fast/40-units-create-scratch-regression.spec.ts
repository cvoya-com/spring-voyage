import { request } from "@playwright/test";

import { unitName } from "../../fixtures/ids.js";
import { AGENT_ID, PROVIDER_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Regression for PR #1598 — the new-unit wizard's scratch branch (#1563)
 * was building a `package.yaml` document the v0.1 PackageManifest schema
 * cannot parse (`kind: Package` with `spec.units[]`, plus indentation
 * bugs in the hand-rolled YAML). The fix routes the scratch branch
 * through `POST /api/v1/tenant/units` + `PUT /…/execution` until the
 * manifest gains inline-artefact support (#1599).
 *
 * Walks the same flow an operator does and asserts the BOTH the unit row
 * AND the execution row exist with the wizard-supplied values — earlier
 * scratch specs only assert UI state, which left the wire-shape failure
 * mode uncovered.
 *
 * Driven inline. The shared `createScratchUnit` helper in
 * `helpers/unit-wizard.ts` still drives the pre-#1563 6-step wizard
 * (mode-card step + `create-unit-button`); reusing it would mask the
 * new flow this regression is meant to lock down. Helper repair is
 * tracked separately.
 */
test.describe("units — wizard scratch end-to-end (regression for #1598)", () => {
  test("scratch path persists the unit and execution rows", async ({
    page,
    tracker,
    baseURL,
  }) => {
    const slug = tracker.unit(unitName("wiz-scratch-regr"));
    const image = "localhost/spring-voyage-agent:latest";

    // Source: Scratch
    await page.goto("/units/create");
    await page.getByTestId("source-card-scratch").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Identity: slug + top-level
    await page.getByRole("textbox", { name: /name \*/i }).fill(slug);
    await page.getByTestId("parent-choice-top-level").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Execution: dapr-agent + ollama + the dapr image
    await page.getByLabel("Agent Runtime").selectOption(AGENT_ID);
    await page.getByLabel("LLM provider").selectOption(PROVIDER_ID);
    await page.getByLabel("Execution image").fill(image);
    await page.getByRole("button", { name: /^next$/i }).click();

    // Connector: skip
    await page.getByRole("button", { name: /^next$/i }).click();

    // Install
    await page.getByTestId("install-unit-button").click();
    await page.waitForURL("**/units**", { timeout: 30_000 });

    // Assert the unit row + execution row reflect the wizard inputs.
    // Cleanup is the tracker fixture's responsibility; we only verify here.
    // #1738: `unit.tool` was retired in #1732. The runtime registry id
    // now lives on the execution block as `agent`; the server derives
    // `toolKind` and exposes it read-only.
    const api = await request.newContext({
      baseURL: baseURL ?? "http://localhost",
    });
    try {
      const unitResp = await api.get(`/api/v1/tenant/units/${slug}`);
      expect(unitResp.ok()).toBeTruthy();
      const unitBody = (await unitResp.json()) as {
        unit: {
          name: string;
          provider: string | null;
        };
      };
      expect(unitBody.unit.name).toBe(slug);
      expect(unitBody.unit.provider).toBe(PROVIDER_ID);

      const execResp = await api.get(`/api/v1/tenant/units/${slug}/execution`);
      expect(execResp.ok()).toBeTruthy();
      const execBody = (await execResp.json()) as {
        image: string | null;
        agent: string | null;
      };
      expect(execBody.image).toBe(image);
      expect(execBody.agent).toBe(AGENT_ID);
    } finally {
      await api.dispose();
    }
  });
});
