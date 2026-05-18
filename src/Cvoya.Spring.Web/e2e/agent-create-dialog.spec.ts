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

const inheritedExecution = {
  image: "ghcr.io/cvoya-com/spring-voyage-agent:latest",
  runtime: "spring-voyage",
  model: { provider: "anthropic", id: "claude-sonnet-4-6" },
};

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
  const unexpectedApiErrors: string[] = [];

  page.on("response", (response) => {
    const url = new URL(response.url());
    if (
      url.pathname.startsWith("/api/v1/tenant/") &&
      response.status() >= 400
    ) {
      unexpectedApiErrors.push(
        `${response.status()} ${response.request().method()} ${url.pathname}`,
      );
    }
  });

  page.on("requestfailed", (request) => {
    const url = new URL(request.url());
    if (url.pathname.startsWith("/api/v1/tenant/")) {
      unexpectedApiErrors.push(
        `failed ${request.method()} ${url.pathname}: ${
          request.failure()?.errorText ?? "unknown"
        }`,
      );
    }
  });

  await page.route(
    (url) => url.pathname === "/api/v1/tenant/tree",
    async (route) => {
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
    },
  );

  await page.route(
    (url) => url.pathname === `/api/v1/tenant/units/${unit.id}`,
    async (route) => {
      await route.fulfill({ json: unit });
    },
  );

  await page.route(
    (url) => url.pathname === "/api/v1/tenant/units",
    async (route) => {
      await route.fulfill({ json: [unit] });
    },
  );

  await page.route(
    (url) =>
      url.pathname === `/api/v1/tenant/units/${unit.id}/memberships`,
    async (route) => {
      await route.fulfill({ json: memberships });
    },
  );

  await page.route(
    (url) =>
      url.pathname === `/api/v1/tenant/units/${unit.id}/execution` ||
      url.pathname === `/api/v1/tenant/units/${unit.name}/execution`,
    async (route) => {
      await route.fulfill({ json: inheritedExecution });
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
      const request = route.request();
      if (request.method() === "GET") {
        await route.fulfill({ json: agents });
        return;
      }

      if (request.method() === "POST") {
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

      await route.fallback();
    },
  );

  // After the dialog persists the new agent the dashboard re-renders the
  // refreshed Members tab, which mounts a `<RuntimeStatusBadge>` per card
  // and immediately polls `/runtime-status` (#2100). Stub it as a benign
  // `idle` so the proxy doesn't try to dial the unmocked dotnet backend
  // and trip the unexpected-API-error guard at the end of the test.
  await page.route(
    (url) =>
      /^\/api\/v1\/tenant\/(agents|units)\/[^/]+\/runtime-status$/.test(
        url.pathname,
      ),
    async (route) => {
      await route.fulfill({
        json: {
          status: "idle",
          lastUpdated: "2026-01-01T00:00:00.000Z",
          inFlightThreadCount: 0,
          queuedMessageCount: 0,
        },
      });
    },
  );

  // The Members tab (#2270 / #2427) also lists human team-role members and
  // queries `auth/me` to decide whether the current viewer can mutate that
  // grid. Neither is mocked above, so without explicit stubs the smoke
  // build's non-routable backend produces 403/aborted responses that the
  // `unexpectedApiErrors` guard at the end of each test picks up.
  await page.route(
    (url) =>
      /^\/api\/v1\/tenant\/units\/[^/]+\/members\/humans$/.test(url.pathname),
    async (route) => {
      await route.fulfill({ json: [] });
    },
  );

  await page.route(
    (url) => url.pathname === "/api/v1/tenant/auth/me",
    async (route) => {
      await route.fulfill({
        json: {
          userId: "viewer",
          displayName: "Viewer",
          id: "00000000-0000-0000-0000-000000000000",
          address: null,
        },
      });
    },
  );

  return { agents, createRequests, memberships, unexpectedApiErrors };
}

async function openCreateDialogFromMembersTab(page: Page) {
  await page.goto(`/units?node=${unit.id}&tab=Members`);

  await expect(page.getByTestId("detail-title")).toHaveText(unit.displayName);
  await expect(page.getByRole("button", { name: /add agent/i })).toBeEnabled();

  await page.getByRole("button", { name: /add agent/i }).click();

  const dialog = page.getByRole("dialog", {
    name: `Create agent in ${unit.displayName}`,
  });
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText(
    `This agent will be registered in ${unit.displayName}`,
  );
  await expect(page.getByTestId("agent-create-dialog-unit-strip")).toContainText(
    unit.displayName,
  );
  await expect(page.getByLabel(`Assign to ${unit.displayName}`)).toBeChecked();

  return dialog;
}

async function fillIdentityAndSubmit(page: Page, displayName = "Ada") {
  await page.getByLabel("Agent id").fill("ada");
  await page.getByLabel("Display name").fill(displayName);
  await page.getByTestId("agent-create-submit").click();
}

test.describe("agent create dialog", () => {
  test("submit without execution overrides sends definitionJson null", async ({
    page,
  }) => {
    const api = await mockDialogFlowApis(page);
    const dialog = await openCreateDialogFromMembersTab(page);

    await fillIdentityAndSubmit(page);

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
    expect(api.unexpectedApiErrors).toEqual([]);
  });

  test("submit with runtime override sends definitionJson", async ({
    page,
  }) => {
    const api = await mockDialogFlowApis(page);

    await openCreateDialogFromMembersTab(page);
    await page.getByLabel("Agent runtime").selectOption("spring-voyage");
    await fillIdentityAndSubmit(page, "Ada Runtime");

    await expect.poll(() => api.createRequests.length).toBe(1);
    const request = api.createRequests[0] as {
      definitionJson: string | null;
      unitIds: string[];
    };

    expect(request.unitIds).toEqual([unit.id]);
    expect(request.definitionJson).not.toBeNull();
    expect(JSON.parse(request.definitionJson ?? "")).toMatchObject({
      runtime: "spring-voyage",
    });
    expect(api.unexpectedApiErrors).toEqual([]);
  });
});
