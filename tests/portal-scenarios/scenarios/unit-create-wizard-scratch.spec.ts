import { test, expect, request } from "@playwright/test";

/**
 * Regression for the wizard's scratch branch — the YAML-manifest path
 * (`POST /api/v1/packages/install/file`) was building a manifest the
 * backend cannot parse. The fix routes the scratch branch through
 * `POST /api/v1/tenant/units` + `PUT /api/v1/tenant/units/{name}/execution`
 * so a unit is actually created end-to-end.
 *
 * Walk the same flow an operator does:
 *   Source = Scratch → Identity (name + top-level) → Execution
 *   (dapr-agent + ollama + dapr image) → skip Connector → Install.
 * Then assert the unit row + execution row exist.
 */
test("wizard scratch path creates a unit + execution row", async ({ page, baseURL }) => {
  const slug = `scn-wizard-${Date.now().toString(36)}`;
  const apiBase = baseURL ?? "http://localhost";

  // ---- 1. Source: Scratch ------------------------------------------
  await page.goto("/units/create");
  await page.getByTestId("source-card-scratch").click();
  await page.getByRole("button", { name: /^next$/i }).click();

  // ---- 2. Identity: slug + top-level -------------------------------
  await page
    .getByRole("textbox", { name: /name \*/i })
    .fill(slug);
  await page.getByTestId("parent-choice-top-level").click();
  await page.getByRole("button", { name: /^next$/i }).click();

  // ---- 3. Execution: dapr-agent + ollama + dapr image --------------
  await page.getByLabel("Execution tool").selectOption("dapr-agent");
  await page.getByLabel("LLM provider").selectOption("ollama");
  await page
    .getByLabel("Execution image")
    .fill("localhost/spring-voyage-agent-dapr:latest");
  await page.getByRole("button", { name: /^next$/i }).click();

  // ---- 4. Connector: skip ------------------------------------------
  await page.getByRole("button", { name: /^next$/i }).click();

  // ---- 5. Install --------------------------------------------------
  await page.getByTestId("install-unit-button").click();
  await page.waitForURL("**/units**", { timeout: 30_000 });

  // ---- Assert backing rows exist -----------------------------------
  const api = await request.newContext({ baseURL: apiBase });
  try {
    const unitResp = await api.get(`/api/v1/tenant/units/${slug}`);
    expect(unitResp.ok()).toBeTruthy();
    const unitBody = (await unitResp.json()) as {
      unit: {
        name: string;
        tool: string | null;
        provider: string | null;
        model: string | null;
      };
    };
    expect(unitBody.unit.name).toBe(slug);
    expect(unitBody.unit.tool).toBe("dapr-agent");
    expect(unitBody.unit.provider).toBe("ollama");

    const execResp = await api.get(`/api/v1/tenant/units/${slug}/execution`);
    expect(execResp.ok()).toBeTruthy();
    const execBody = (await execResp.json()) as { image: string | null };
    expect(execBody.image).toBe("localhost/spring-voyage-agent-dapr:latest");
  } finally {
    // Inline cleanup — there is no fixture-level tracker in this suite.
    await api.delete(`/api/v1/tenant/units/${slug}`).catch(() => {});
    await api.dispose();
  }
});
