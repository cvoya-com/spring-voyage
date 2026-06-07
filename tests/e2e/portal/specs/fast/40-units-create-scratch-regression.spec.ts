import { request } from "@playwright/test";

import { resolveUnitIdByDisplayName } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { AGENT_ID, PROVIDER_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Regression for PR #1598 — the new-unit wizard's scratch branch (#1563)
 * was building a `package.yaml` document the v0.1 PackageManifest schema
 * cannot parse. The fix routes the scratch branch through
 * `POST /api/v1/tenant/units` + `PUT /…/execution`.
 *
 * Walks the same flow an operator does and asserts BOTH the unit row AND
 * the execution row exist with the wizard-supplied values — earlier
 * scratch specs only assert UI state, which left the wire-shape failure
 * mode uncovered.
 *
 * Driven inline (not via the shared `createScratchUnit` helper) so the
 * wire-shape assertions stay coupled to the literal wizard steps this
 * regression locks down.
 *
 * Identity model (#2473): the wizard's "Name" field becomes the unit's
 * displayName; the server assigns the hex id. Execution is keyed on the
 * hex and carries `runtime` (the operator-chosen runtime id) — the legacy
 * flat `unit.provider` / `execution.agent` slots are gone (ADR-0038).
 */
test.describe("units — wizard scratch end-to-end (regression for #1598)", () => {
  test("scratch path persists the unit and execution rows", async ({
    page,
    tracker,
    baseURL,
  }) => {
    const slug = tracker.unit(unitName("wiz-scratch-regr"));
    const image = "ghcr.io/cvoya-com/spring-voyage-agent:latest";

    // Source: Scratch
    await page.goto("/units/create");
    await page.getByTestId("source-card-scratch").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Identity: slug + top-level
    await page.getByRole("textbox", { name: /^name/i }).first().fill(slug);
    await page.getByTestId("parent-choice-top-level").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Execution: dapr-agent + ollama + a model + the dapr image.
    await page.getByTestId("unit-create-runtime-select").selectOption(AGENT_ID);
    await page
      .getByTestId("unit-create-model-provider-select")
      .selectOption(PROVIDER_ID);
    const modelSelect = page.getByTestId("unit-create-model-select");
    await modelSelect.waitFor({ state: "visible", timeout: 30_000 });
    const values = await modelSelect.evaluate((el) =>
      Array.from((el as HTMLSelectElement).options).map((o) => o.value),
    );
    if (values.length === 0) test.skip(true, "Ollama returned no models");
    await modelSelect.selectOption(values[0]!);
    await page.getByLabel("Execution image").fill(image);
    await page.getByRole("button", { name: /^next$/i }).click();

    // Connector: skip
    await page.getByRole("button", { name: /^next$/i }).click();

    // Install. The scratch branch POSTs the unit synchronously then
    // validates before its own redirect (slow on a cold stack), so resolve
    // the server-assigned hex by polling the API rather than waiting on the
    // wizard URL change.
    await page.getByTestId("install-unit-button").click();
    await expect
      .poll(async () => resolveUnitIdByDisplayName(slug), {
        timeout: 90_000,
        intervals: [500, 1000, 2000],
      })
      .not.toBeNull();
    const id = await resolveUnitIdByDisplayName(slug);
    const hex = id!.replace(/-/g, "");

    // Assert the unit row + execution row reflect the wizard inputs.
    // Cleanup is the tracker fixture's responsibility; we only verify here.
    const api = await request.newContext({
      baseURL: baseURL ?? "http://localhost",
    });
    try {
      const unitResp = await api.get(`/api/v1/tenant/units/${hex}`);
      expect(unitResp.ok()).toBeTruthy();
      const unitBody = (await unitResp.json()) as {
        unit: { name: string; displayName: string };
      };
      // The slug lands in displayName; `name` is the server hex id.
      expect(unitBody.unit.displayName).toBe(slug);

      const execResp = await api.get(`/api/v1/tenant/units/${hex}/execution`);
      expect(execResp.ok()).toBeTruthy();
      const execBody = (await execResp.json()) as {
        image: string | null;
        runtime: string | null;
        model: { provider: string | null; id: string | null } | null;
      };
      expect(execBody.image).toBe(image);
      // ADR-0038: the runtime registry id lives on the execution block as
      // `runtime` (the legacy `agent`/`tool` slots were retired).
      expect(execBody.runtime).toBe(AGENT_ID);
      expect(execBody.model?.provider).toBe(PROVIDER_ID);
    } finally {
      await api.dispose();
    }
  });
});
