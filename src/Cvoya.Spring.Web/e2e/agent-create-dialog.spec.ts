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

type AgentFixture = {
  id: string;
  name: string;
  displayName: string;
  description: string;
  role: string | null;
  registeredAt: string;
  model: null;
  specialty: null;
  enabled: boolean;
  executionMode: "Auto";
  parentUnit: string | null;
  hostingMode: null;
  initiativeLevel: null;
};

function makeAgent(name: string, displayName: string): AgentFixture {
  return {
    id: `${name}-id`,
    name,
    displayName,
    description: "",
    role: null,
    registeredAt: "2026-01-01T00:00:00.000Z",
    model: null,
    specialty: null,
    enabled: true,
    executionMode: "Auto",
    parentUnit: unit.name,
    hostingMode: null,
    initiativeLevel: null,
  };
}

function makeMembership(agentName: string) {
  const now = "2026-01-01T00:00:00.000Z";
  return {
    unitId: unit.id,
    agentAddress: agentName,
    member: `agent://${agentName}`,
    model: null,
    specialty: null,
    enabled: true,
    executionMode: "Auto",
    isPrimary: true,
    createdAt: now,
    updatedAt: now,
  };
}

async function mockDialogFlowApis(page: Page) {
  const agents: AgentFixture[] = [];
  const memberships: ReturnType<typeof makeMembership>[] = [];
  const createRequests: unknown[] = [];

  await page.route("**/api/v1/tenant/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();
    const path = url.pathname;

    if (method === "GET" && path === "/api/v1/tenant/tree") {
      await route.fulfill({
        json: {
          tree: {
            id: "tenant://default",
            name: "Default tenant",
            kind: "Tenant",
            status: "running",
            children: [
              {
                id: unit.id,
                name: unit.displayName,
                kind: "Unit",
                status: "stopped",
                children: agents.map((agent) => ({
                  id: agent.name,
                  name: agent.displayName,
                  kind: "Agent",
                  status: "running",
                  primaryParentId: unit.id,
                })),
              },
            ],
          },
        },
      });
      return;
    }

    if (method === "GET" && path === `/api/v1/tenant/units/${unit.id}`) {
      await route.fulfill({ json: unit });
      return;
    }

    if (method === "GET" && path === "/api/v1/tenant/units") {
      await route.fulfill({ json: [unit] });
      return;
    }

    if (
      method === "GET" &&
      path === `/api/v1/tenant/units/${unit.id}/memberships`
    ) {
      await route.fulfill({ json: memberships });
      return;
    }

    if (
      method === "GET" &&
      path === `/api/v1/tenant/units/${unit.name}/execution`
    ) {
      await route.fulfill({
        json: {
          image: "ghcr.io/cvoya-com/spring-voyage-agent:latest",
          runtime: "spring-voyage",
          model: { provider: "anthropic", id: "claude-sonnet-4-6" },
        },
      });
      return;
    }

    if (
      method === "GET" &&
      path === "/api/v1/tenant/model-providers/installs"
    ) {
      await route.fulfill({ json: providers });
      return;
    }

    if (
      method === "GET" &&
      path === "/api/v1/tenant/model-providers/installs/anthropic/models"
    ) {
      await route.fulfill({
        json: [{ id: "claude-sonnet-4-6", displayName: "Claude Sonnet 4.6" }],
      });
      return;
    }

    if (path === "/api/v1/tenant/agents") {
      if (method === "GET") {
        await route.fulfill({ json: agents });
        return;
      }

      if (method === "POST") {
        const body = request.postDataJSON();
        createRequests.push(body);
        const displayName =
          typeof body.displayName === "string" ? body.displayName : "Ada";
        const created = makeAgent("ada", displayName);
        agents.push(created);
        memberships.push(makeMembership(created.name));
        await route.fulfill({ status: 201, json: created });
        return;
      }
    }

    await route.fulfill({
      status: 404,
      json: { error: `Unhandled ${method} ${path}` },
    });
  });

  return { agents, createRequests, memberships };
}

test.describe("agent create dialog", () => {
  test("creates an inherited agent from the unit Agents tab", async ({
    page,
  }) => {
    const api = await mockDialogFlowApis(page);

    await page.goto(`/units?node=${unit.id}&tab=Agents`);

    await expect(page.getByTestId("detail-title")).toHaveText(
      unit.displayName,
    );
    await expect(page.getByRole("button", { name: /add agent/i })).toBeEnabled();

    await page.getByRole("button", { name: /add agent/i }).click();

    const dialog = page.getByRole("dialog", {
      name: `Create agent in ${unit.displayName}`,
    });
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText(
      `This agent will be registered in ${unit.displayName}`,
    );
    await expect(
      page.getByTestId("agent-create-dialog-unit-strip"),
    ).toContainText(unit.displayName);
    await expect(
      page.getByLabel(`Assign to ${unit.displayName}`),
    ).toBeChecked();

    await page.getByLabel("Agent id").fill("ada");
    await page.getByLabel("Display name").fill("Ada");

    await page.getByTestId("agent-create-submit").click();

    await expect.poll(() => api.createRequests.length).toBe(1);
    expect(api.createRequests[0]).toMatchObject({
      displayName: "Ada",
      description: "",
      role: null,
      unitIds: [unit.id],
      definitionJson: null,
    });

    await expect(dialog).toBeHidden();
    await expect(page.getByTestId("agent-card-ada")).toContainText("Ada");
    expect(api.agents).toHaveLength(1);
    expect(api.memberships).toHaveLength(1);
  });
});
