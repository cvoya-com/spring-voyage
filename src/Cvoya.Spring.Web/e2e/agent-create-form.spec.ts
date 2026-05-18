import { expect, type Page, test } from "@playwright/test";

const unit = {
  id: "unit-alpha-id",
  name: "engineering",
  displayName: "Engineering Team",
  description: "Engineering parent unit",
  registeredAt: "2026-01-01T00:00:00.000Z",
  status: "Stopped",
  model: null,
  color: null,
  hosting: null,
};

const providers = [
  {
    id: "anthropic",
    displayName: "Anthropic",
    installedAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
    models: ["claude-sonnet-4-6"],
    defaultModel: "claude-sonnet-4-6",
    baseUrl: null,
    credentialKind: "ApiKey",
    credentialDisplayHint: null,
    credentialSecretName: "anthropic-api-key",
  },
];

async function mockAgentCreateApis(
  page: Page,
  options: { rejectFirstCreate?: boolean } = {},
) {
  const createRequests: unknown[] = [];

  await page.route(
    (url) => url.pathname === "/api/v1/tenant/units",
    async (route) => {
      if (route.request().method() === "GET") {
        await route.fulfill({ json: [unit] });
        return;
      }
      await route.fallback();
    },
  );

  await page.route(
    (url) => url.pathname === "/api/v1/tenant/units/engineering/execution",
    async (route) => {
      await route.fulfill({
        json: {
          image: "ghcr.io/cvoya-com/spring-voyage-agent:latest",
          runtime: "spring-voyage",
          model: { provider: "anthropic", id: "claude-sonnet-4-6" },
        },
      });
    },
  );

  await page.route(
    (url) => url.pathname === "/api/v1/tenant/model-providers/installs",
    async (route) => {
      await route.fulfill({ json: providers });
    },
  );

  await page.route(
    (url) =>
      url.pathname ===
      "/api/v1/tenant/model-providers/installs/anthropic/models",
    async (route) => {
      await route.fulfill({
        json: [{ id: "claude-sonnet-4-6", displayName: "Claude Sonnet 4.6" }],
      });
    },
  );

  await page.route(
    (url) => url.pathname === "/api/v1/tenant/agents",
    async (route) => {
      if (route.request().method() !== "POST") {
        await route.fulfill({ json: [] });
        return;
      }

      createRequests.push(route.request().postDataJSON());

      if (options.rejectFirstCreate && createRequests.length === 1) {
        await route.fulfill({
          status: 422,
          contentType: "application/problem+json",
          json: {
            type:
              "https://docs.cvoya.com/spring/errors/multi-parent-inheritance-conflict",
            title: "Multi-parent inheritance conflict",
            status: 422,
            detail:
              "Inherited execution-config fields disagree across parent units.",
            error: "MultiParentInheritanceConflict",
            conflictingFields: {
              runtime: [
                { source: "unit-engineering", value: "claude-code" },
                { source: "unit-support", value: "spring-voyage" },
              ],
            },
          },
        });
        return;
      }

      await route.fulfill({
        json: {
          id: "agent-ada-id",
          name: "ada",
          displayName: "Ada",
          description: "",
          role: null,
          registeredAt: "2026-01-01T00:00:00.000Z",
          model: null,
          specialty: null,
          enabled: true,
          executionMode: "Respond",
          parentUnit: null,
          hostingMode: null,
          initiativeLevel: null,
        },
      });
    },
  );

  return { createRequests };
}

async function openScratchAgentCreate(page: Page) {
  await page.goto("/agents/create");
  await page.getByTestId("agent-source-card-scratch").click();
  await page.getByRole("button", { name: /^next$/i }).click();
  await expect(page.getByLabel("Agent id")).toBeVisible();
}

test.describe("agent create form", () => {
  test("shows inherit indicators and badge on the scratch path", async ({
    page,
  }) => {
    await mockAgentCreateApis(page);
    await openScratchAgentCreate(page);

    await expect(page.getByTestId("execution-card-badge")).toHaveText(
      "Inherits",
    );

    await page
      .getByRole("checkbox", { name: /assign to engineering team/i })
      .check();

    await expect(page.getByLabel("Model provider")).toBeVisible();
    await expect(page.getByTestId("inherit-indicator")).toHaveCount(5);
    await expect(page.getByTestId("inherit-indicator").first()).toContainText(
      "inherited from Engineering Team",
    );
  });

  test("renders a structured conflict inline, then allows corrected resubmit", async ({
    page,
  }) => {
    const api = await mockAgentCreateApis(page, { rejectFirstCreate: true });
    await openScratchAgentCreate(page);

    await page.getByLabel("Agent id").fill("ada");
    await page.getByLabel("Display name").fill("Ada");
    await page
      .getByRole("checkbox", { name: /assign to engineering team/i })
      .check();

    await page.getByTestId("agent-create-submit").click();

    const block = page.getByTestId("multi-parent-inheritance-conflict");
    await expect(block).toBeVisible();
    await expect(block).toContainText("runtime");
    await expect(block).toContainText("claude-code");
    await expect(block).toContainText("spring-voyage");
    await expect(page.getByText("Agent create blocked")).toHaveCount(0);
    await expect(page.getByTestId("agent-create-submit")).toBeDisabled();

    await page.getByLabel("Agent runtime").selectOption("spring-voyage");

    await expect(block).toBeHidden();
    await expect(page.getByTestId("execution-card-badge")).toHaveText(
      "Configured",
    );
    await expect(page.getByTestId("agent-create-submit")).toBeEnabled();

    await page.getByTestId("agent-create-submit").click();
    await expect(page).toHaveURL(/\/units\?node=engineering&tab=Members$/);
    expect(api.createRequests).toHaveLength(2);
  });
});
