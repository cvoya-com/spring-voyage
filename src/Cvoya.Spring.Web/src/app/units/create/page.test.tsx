import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import { expectNoAxeViolations } from "@/test/a11y";
import type {
  InstalledAgentRuntimeResponse,
  ProviderCredentialStatusResponse,
} from "@/lib/api/types";

// Mock the API client. Post-T-07 (#949) the wizard no longer validates
// the credential against the LLM at the host — validation runs as a
// backend Dapr workflow after create. So there is no
// `validateAgentRuntimeCredential` mock; the wizard just reads the
// agent-runtime catalog + models + persists the credential.
const listOllamaModels = vi.fn();
const listAgentRuntimes = vi.fn();
const getAgentRuntimeModels = vi.fn();
const getProviderCredentialStatus = vi.fn();
const createUnit = vi.fn();
const createUnitFromTemplate = vi.fn();
const createUnitFromYaml = vi.fn();
const createUnitSecret = vi.fn();
const createTenantSecret = vi.fn();
const rotateTenantSecret = vi.fn();
const startUnit = vi.fn();
const getUnit = vi.fn();
const getUnitExecution = vi.fn();

const listUnitTemplates = vi.fn();
const listConnectorTypes = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    listOllamaModels: () => listOllamaModels(),
    listAgentRuntimes: () => listAgentRuntimes(),
    getAgentRuntimeModels: (id: string) => getAgentRuntimeModels(id),
    getProviderCredentialStatus: (p: string) => getProviderCredentialStatus(p),
    // Legacy aliases kept for tests that still reference the old names.
    getUnitTemplates: vi.fn().mockResolvedValue([]),
    getConnectorTypes: vi.fn().mockResolvedValue([]),
    // Real API method names — `useUnitTemplates` / `useConnectorTypes`
    // reach through `api.listUnitTemplates` / `api.listConnectorTypes`,
    // so tests that exercise template or connector flows must mock these
    // explicitly (seedDefaultMocks below seeds `[]` as the default).
    listUnitTemplates: () => listUnitTemplates(),
    listConnectorTypes: () => listConnectorTypes(),
    createUnit: (body: unknown) => createUnit(body),
    createUnitFromTemplate: (body: unknown) => createUnitFromTemplate(body),
    createUnitFromYaml: (body: unknown) => createUnitFromYaml(body),
    createUnitSecret: (unit: string, body: unknown) =>
      createUnitSecret(unit, body),
    createTenantSecret: (body: unknown) => createTenantSecret(body),
    rotateTenantSecret: (name: string, body: unknown) =>
      rotateTenantSecret(name, body),
    startUnit: (name: string) => startUnit(name),
    getUnit: (name: string) => getUnit(name),
    getUnitExecution: (name: string) => getUnitExecution(name),
    revalidateUnit: vi.fn().mockResolvedValue(undefined),
  },
}));

// The Finalize step mounts ValidationPanel, which subscribes to
// `useActivityStream`. Stub it so the test doesn't try to open a real
// EventSource under JSDOM.
vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: true }),
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

const pushMock = vi.fn();
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: pushMock }),
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: React.ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

import CreateUnitPage from "./page";

function makeStatus(
  overrides: Partial<ProviderCredentialStatusResponse>,
): ProviderCredentialStatusResponse {
  return {
    provider: "anthropic",
    resolvable: true,
    source: "tenant",
    suggestion: null,
    ...overrides,
  };
}

function makeRuntime(
  overrides: Partial<InstalledAgentRuntimeResponse>,
): InstalledAgentRuntimeResponse {
  const now = new Date().toISOString();
  return {
    id: "claude",
    displayName: "Claude",
    toolKind: "claude-code-cli",
    installedAt: now,
    updatedAt: now,
    models: ["claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5"],
    defaultModel: "claude-opus-4-7",
    baseUrl: null,
    credentialKind: "ApiKey",
    credentialDisplayHint: null,
    ...overrides,
  } as InstalledAgentRuntimeResponse;
}

function defaultRuntimes(): InstalledAgentRuntimeResponse[] {
  return [
    makeRuntime({
      id: "claude",
      displayName: "Claude (Claude Code CLI + Anthropic API)",
      toolKind: "claude-code-cli",
      models: [
        "claude-opus-4-7",
        "claude-sonnet-4-6",
        "claude-haiku-4-5",
      ],
      defaultModel: "claude-opus-4-7",
      credentialKind: "ApiKey",
    }),
    makeRuntime({
      id: "openai",
      displayName: "OpenAI (dapr-agent + OpenAI API)",
      toolKind: "dapr-agent",
      models: ["gpt-4o", "gpt-4o-mini", "o3-mini"],
      defaultModel: "gpt-4o",
      credentialKind: "ApiKey",
    }),
    makeRuntime({
      id: "google",
      displayName: "Google AI (dapr-agent + Google AI API)",
      toolKind: "dapr-agent",
      models: ["gemini-2.5-pro", "gemini-2.5-flash"],
      defaultModel: "gemini-2.5-pro",
      credentialKind: "ApiKey",
    }),
    makeRuntime({
      id: "ollama",
      displayName: "Ollama (dapr-agent + local Ollama)",
      toolKind: "dapr-agent",
      models: ["qwen2.5:14b", "llama3.2:3b"],
      defaultModel: "qwen2.5:14b",
      credentialKind: "None",
    }),
  ];
}

function renderPage() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
      mutations: { retry: false },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<CreateUnitPage />, { wrapper: Wrapper });
}

async function advanceToExecution() {
  const nameInput = screen.getByPlaceholderText(
    /engineering-team/i,
  ) as HTMLInputElement;
  if (nameInput.value === "") {
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "acme" } });
    });
  }
  const next = screen.getByRole("button", { name: /^next$/i });
  await act(async () => {
    fireEvent.click(next);
  });
}

async function selectTool(value: string) {
  const toolSelect = screen.getByLabelText("Execution tool") as HTMLSelectElement;
  await act(async () => {
    fireEvent.change(toolSelect, { target: { value } });
  });
}

function seedDefaultMocks() {
  listOllamaModels.mockResolvedValue([]);
  listAgentRuntimes.mockResolvedValue(defaultRuntimes());
  getAgentRuntimeModels.mockImplementation(async (id: string) => {
    const runtime = defaultRuntimes().find((r) => r.id === id);
    return (runtime?.models ?? []).map((m) => ({
      id: m,
      displayName: m,
      contextWindow: null,
    }));
  });
  getProviderCredentialStatus.mockResolvedValue(
    makeStatus({ provider: "anthropic", resolvable: true, source: "tenant" }),
  );
  // Defaults — tests that exercise template / connector flows override
  // these. Keeping them on the seed path means the list-templates /
  // list-connector-types queries never throw and the wizard renders in
  // its empty-template-catalog state by default.
  listUnitTemplates.mockResolvedValue([]);
  listConnectorTypes.mockResolvedValue([]);
}

describe("CreateUnitPage — wizard reads tenant-installed agent runtimes (#690)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
  });

  it("hides the Provider dropdown when the tool is Claude Code", async () => {
    renderPage();
    await advanceToExecution();

    expect(
      screen.queryByLabelText(/^LLM provider$/i),
    ).not.toBeInTheDocument();
    expect(await screen.findByLabelText(/^Model$/i)).toBeInTheDocument();
  });

  it("populates the Model dropdown from GET /api/v1/agent-runtimes/{id}/models", async () => {
    renderPage();
    await advanceToExecution();

    const modelSelect = (await screen.findByLabelText(
      /^Model$/i,
    )) as HTMLSelectElement;

    await waitFor(() => {
      expect(getAgentRuntimeModels).toHaveBeenCalledWith("claude");
    });

    const options = Array.from(modelSelect.options).map((o) => o.value);
    expect(options).toContain("claude-sonnet-4-6");
    expect(options).toContain("claude-opus-4-7");
    expect(options).toContain("claude-haiku-4-5");
  });

  it("switches to the openai runtime catalog when Tool=Codex", async () => {
    renderPage();
    await advanceToExecution();
    await selectTool("codex");

    expect(screen.queryByLabelText(/^LLM provider$/i)).not.toBeInTheDocument();

    await waitFor(() => {
      expect(getAgentRuntimeModels).toHaveBeenCalledWith("openai");
    });

    const modelSelect = (await screen.findByLabelText(
      /^Model$/i,
    )) as HTMLSelectElement;
    const options = Array.from(modelSelect.options).map((o) => o.value);
    expect(options).toContain("gpt-4o");
  });

  it("shows installed dapr-agent runtimes in the Provider dropdown", async () => {
    renderPage();
    await advanceToExecution();
    await selectTool("dapr-agent");

    const providerSelect = (await screen.findByLabelText(
      /^LLM provider$/i,
    )) as HTMLSelectElement;
    const options = Array.from(providerSelect.options).map((o) => o.value);

    // Only dapr-agent runtimes are listed — the claude runtime's
    // toolKind is claude-code-cli and is filtered out.
    expect(options).toContain("openai");
    expect(options).toContain("google");
    expect(options).toContain("ollama");
    expect(options).not.toContain("claude");
  });

  it("hides the credential input for runtimes with CredentialKind=None (ollama)", async () => {
    renderPage();
    await advanceToExecution();
    await selectTool("dapr-agent");

    const providerSelect = screen.getByLabelText(
      /^LLM provider$/i,
    ) as HTMLSelectElement;
    await act(async () => {
      fireEvent.change(providerSelect, { target: { value: "ollama" } });
    });

    // The credential input is hidden on Ollama — no API key to validate.
    expect(screen.queryByTestId("credential-input")).not.toBeInTheDocument();
  });
});

describe("CreateUnitPage — credential-status banner (#598, preserved post-T-07)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
  });

  it("renders a tenant-default hint when credentials inherit from tenant", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "anthropic", resolvable: true, source: "tenant" }),
    );

    renderPage();
    await advanceToExecution();

    const status = await screen.findByTestId("credential-status");
    expect(status.dataset.resolvable).toBe("true");
    expect(status.dataset.source).toBe("tenant");
    expect(status.textContent).toMatch(/inherited from tenant default/i);
  });

  it("renders an inline credential input when credentials are not configured", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({
        provider: "anthropic",
        resolvable: false,
        source: null,
        suggestion:
          "Anthropic credentials are not configured. Set the tenant-default secret 'anthropic-api-key' …",
      }),
    );

    renderPage();
    await advanceToExecution();

    const status = await screen.findByTestId("credential-status");
    expect(status.dataset.resolvable).toBe("false");
    expect(status.textContent).toMatch(/not configured/i);
    expect(screen.getByTestId("credential-input")).toBeInTheDocument();
    expect(
      screen.getByTestId("credential-save-as-tenant-default"),
    ).toBeInTheDocument();
  });

  it("passes axe a11y smoke with the warning banner visible", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({
        provider: "anthropic",
        resolvable: false,
        source: null,
        suggestion: "Anthropic credentials are not configured.",
      }),
    );

    const { container } = renderPage();
    await advanceToExecution();
    await screen.findByTestId("credential-status");
    await expectNoAxeViolations(container);
  });
});

// Issue #978 — two wizard dead-ends around the credential flow.
describe("CreateUnitPage — #978 wizard credential dead-ends", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
  });

  it("defect 2: renders the credential input when the probe fails (statusError branch)", async () => {
    // Simulate the pre-sibling-PR backend returning 500 for unreadable
    // ciphertext: the query's queryFn catches and returns null.
    getProviderCredentialStatus.mockResolvedValue(null);

    renderPage();
    await advanceToExecution();

    // The input must be present even though the probe didn't resolve,
    // so the user has an escape hatch out of the Finalize dead-end.
    expect(await screen.findByTestId("credential-input")).toBeInTheDocument();
    expect(
      screen.getByTestId("credential-save-as-tenant-default"),
    ).toBeInTheDocument();
    const status = screen.getByTestId("credential-status");
    expect(status.textContent).toMatch(/could not verify/i);
  });

  it("defect 2: renders the credential input when the probe throws", async () => {
    getProviderCredentialStatus.mockRejectedValue(
      new Error("API error 500: Internal Server Error"),
    );

    renderPage();
    await advanceToExecution();

    // Even on a throw the queries hook swallows and returns null, so the
    // UI must still surface the input.
    expect(await screen.findByTestId("credential-input")).toBeInTheDocument();
  });

  it("defect 3: step 5 shows the tenant-default row when source=tenant", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "anthropic", resolvable: true, source: "tenant" }),
    );

    renderPage();
    // Identity
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "acme" } });
    });
    const clickNext = async () => {
      const next = screen.getByRole("button", { name: /^next$/i });
      await act(async () => {
        fireEvent.click(next);
      });
    };
    await clickNext(); // → Execution
    // Wait for the model dropdown so Next is enabled.
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });
    await clickNext(); // → Mode
    const scratch = screen.getByRole("button", { name: /scratch/i });
    await act(async () => {
      fireEvent.click(scratch);
    });
    await clickNext(); // → Connector
    await clickNext(); // → Secrets

    const row = await screen.findByTestId("tenant-default-secret-row");
    expect(row.getAttribute("data-provider")).toBe("anthropic");
    expect(row.textContent).toMatch(/anthropic tenant default/i);
    expect(row.textContent).toContain("anthropic-api-key");
    // "No secrets queued" must not be rendered here — the operator has
    // a tenant default and an override affordance, not an empty slate.
    expect(
      screen.getByTestId("tenant-default-override"),
    ).toBeInTheDocument();
  });

  it("defect 3: clicking Override on step 5 reveals the credential input", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "anthropic", resolvable: true, source: "tenant" }),
    );

    renderPage();
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "acme" } });
    });
    const clickNext = async () => {
      const next = screen.getByRole("button", { name: /^next$/i });
      await act(async () => {
        fireEvent.click(next);
      });
    };
    await clickNext();
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });
    await clickNext();
    const scratch = screen.getByRole("button", { name: /scratch/i });
    await act(async () => {
      fireEvent.click(scratch);
    });
    await clickNext();
    await clickNext();

    const override = await screen.findByTestId("tenant-default-override");
    await act(async () => {
      fireEvent.click(override);
    });
    // The shared credential input + tenant checkbox should now render.
    expect(
      await screen.findByTestId("credential-input"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("credential-save-as-tenant-default"),
    ).toBeInTheDocument();
  });

  it("defect 3: step 5 omits the tenant-default row when source=unit", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "anthropic", resolvable: true, source: "unit" }),
    );

    renderPage();
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "acme" } });
    });
    const clickNext = async () => {
      const next = screen.getByRole("button", { name: /^next$/i });
      await act(async () => {
        fireEvent.click(next);
      });
    };
    await clickNext();
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });
    await clickNext();
    const scratch = screen.getByRole("button", { name: /scratch/i });
    await act(async () => {
      fireEvent.click(scratch);
    });
    await clickNext();
    await clickNext();

    expect(
      screen.queryByTestId("tenant-default-secret-row"),
    ).not.toBeInTheDocument();
    // Legacy "No secrets queued" state is preserved.
    expect(screen.getByText(/no secrets queued/i)).toBeInTheDocument();
  });
});

// T-07 (#949): the Model dropdown now renders against the agent-runtime
// catalog regardless of credential status — Next is never gated on a
// live reach-out to the LLM. The backend validates the key after the
// unit is created; the detail-page Validation panel surfaces the
// outcome.
describe("CreateUnitPage — T-07 wizard simplification (#949)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    createUnit.mockResolvedValue({ name: "acme", id: "acme-id" });
    createUnitSecret.mockResolvedValue({
      name: "anthropic-api-key",
      version: "v1",
    });
    // Default: start succeeds and the polled GET /units/{id} returns a
    // terminal Running status on the first fetch. Tests that care about
    // the intermediate Validating / Error branches override these.
    startUnit.mockResolvedValue(undefined);
    getUnit.mockResolvedValue({
      id: "acme-id",
      name: "acme",
      displayName: "Acme",
      description: "",
      registeredAt: "2026-04-21T00:00:00Z",
      status: "Running",
      model: "claude-opus-4-7",
      color: null,
      tool: "claude-code",
      provider: null,
      hosting: null,
      lastValidationError: null,
      lastValidationRunId: null,
    });
    getUnitExecution.mockResolvedValue({
      unitId: "acme-id",
      image: null,
      runtime: null,
      model: null,
      secrets: null,
      updatedAt: null,
    });
  });

  it("renders the Model dropdown even when credentials aren't resolvable", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "anthropic", resolvable: false, source: null }),
    );
    renderPage();
    await advanceToExecution();

    // Model dropdown is visible without any key having been validated.
    const modelSelect = (await screen.findByLabelText(
      /^Model$/i,
    )) as HTMLSelectElement;
    const options = Array.from(modelSelect.options).map((o) => o.value);
    expect(options).toContain("claude-sonnet-4-6");
  });

  it("advances Next without gating on credential validation", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "anthropic", resolvable: false, source: null }),
    );
    renderPage();
    await advanceToExecution();

    // Wait for the model dropdown to seed a default selection.
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });

    // Next is enabled immediately — no wizard-time validation request.
    const next = screen.getByRole("button", { name: /^next$/i });
    expect(next).not.toBeDisabled();
  });

  it("submits the wizard end-to-end, writing the unit secret and navigating", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "anthropic", resolvable: false, source: null }),
    );
    renderPage();

    // Identity
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "acme" } });
    });
    const nextToExec = screen.getByRole("button", { name: /^next$/i });
    await act(async () => {
      fireEvent.click(nextToExec);
    });

    // Execution — paste a key and advance immediately (no validate call).
    const input = (await screen.findByTestId(
      "credential-input",
    )) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(input, { target: { value: "sk-ant-unit" } });
    });
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });

    const clickNext = async () => {
      const next = screen.getByRole("button", { name: /^next$/i });
      await act(async () => {
        fireEvent.click(next);
      });
    };

    await clickNext(); // Execution → Mode
    const scratch = screen.getByRole("button", { name: /scratch/i });
    await act(async () => {
      fireEvent.click(scratch);
    });
    await clickNext(); // Mode → Connector
    await clickNext(); // Connector → Secrets
    await clickNext(); // Secrets → Finalize

    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(createUnit).toHaveBeenCalledTimes(1);
    });
    expect(createUnitSecret).toHaveBeenCalledWith("acme", {
      name: "anthropic-api-key",
      value: "sk-ant-unit",
    });
    // Wizard auto-starts the unit (#983) and waits for a terminal
    // status before redirecting to the Explorer.
    await waitFor(() => {
      expect(startUnit).toHaveBeenCalledWith("acme");
    });
    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith(
        "/units?node=acme&tab=Overview",
      );
    });
  });
});

// #983 / #980 item 1: the wizard auto-starts the unit after create,
// waits for validation to finish, and routes to the Explorer. On a
// terminal Error it keeps the user on Finalize with a Back affordance.
describe("CreateUnitPage — auto-start + validation (#983)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    createUnit.mockResolvedValue({ name: "acme", id: "acme-id" });
    createUnitSecret.mockResolvedValue({
      name: "anthropic-api-key",
      version: "v1",
    });
    startUnit.mockResolvedValue(undefined);
    getUnitExecution.mockResolvedValue({
      unitId: "acme-id",
      image: null,
      runtime: null,
      model: null,
      secrets: null,
      updatedAt: null,
    });
  });

  async function advanceWizardToFinalize() {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({
        provider: "anthropic",
        resolvable: true,
        source: "tenant",
      }),
    );
    renderPage();
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "acme" } });
    });
    const clickNext = async () => {
      const next = screen.getByRole("button", { name: /^next$/i });
      await act(async () => {
        fireEvent.click(next);
      });
    };
    await clickNext(); // → Execution
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });
    await clickNext(); // → Mode
    const scratch = screen.getByRole("button", { name: /scratch/i });
    await act(async () => {
      fireEvent.click(scratch);
    });
    await clickNext(); // → Connector
    await clickNext(); // → Secrets
    await clickNext(); // → Finalize
  }

  it("success path: POSTs /start, renders ValidationPanel, redirects on Running", async () => {
    getUnit.mockResolvedValue({
      id: "acme-id",
      name: "acme",
      displayName: "Acme",
      description: "",
      registeredAt: "2026-04-21T00:00:00Z",
      status: "Running",
      model: "claude-opus-4-7",
      color: null,
      tool: "claude-code",
      provider: null,
      hosting: null,
      lastValidationError: null,
      lastValidationRunId: null,
    });

    await advanceWizardToFinalize();

    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(createUnit).toHaveBeenCalledTimes(1);
    });
    await waitFor(() => {
      expect(startUnit).toHaveBeenCalledWith("acme");
    });
    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith(
        "/units?node=acme&tab=Overview",
      );
    });
  });

  it("error path: terminal Error keeps the user on Finalize with a Back affordance", async () => {
    getUnit.mockResolvedValue({
      id: "acme-id",
      name: "acme",
      displayName: "Acme",
      description: "",
      registeredAt: "2026-04-21T00:00:00Z",
      status: "Error",
      model: "claude-opus-4-7",
      color: null,
      tool: "claude-code",
      provider: null,
      hosting: null,
      lastValidationError: {
        step: "ValidatingCredential",
        code: "CredentialInvalid",
        message: "Credential rejected",
      },
      lastValidationRunId: "run-123",
    });

    await advanceWizardToFinalize();

    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(startUnit).toHaveBeenCalledWith("acme");
    });

    // Error action row shows up; redirect must NOT happen.
    await screen.findByTestId("wizard-validation-error-actions");
    expect(pushMock).not.toHaveBeenCalled();

    // Back affordance steps the wizard back to Execution (step 2).
    const back = screen.getByTestId("wizard-validation-back");
    await act(async () => {
      fireEvent.click(back);
    });

    // Once the user steps back, the Execution step's Tool select is
    // visible again.
    expect(screen.getByLabelText("Execution tool")).toBeInTheDocument();
  });
});

describe("CreateUnitPage — provider help links (#659)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
  });

  it("renders an Anthropic help link when the derived provider is anthropic", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "anthropic", resolvable: false, source: null }),
    );
    renderPage();
    await advanceToExecution();
    const link = await screen.findByTestId("credential-help-link");
    expect(link.getAttribute("href")).toBe(
      "https://console.anthropic.com/settings/keys",
    );
    expect(link.getAttribute("target")).toBe("_blank");
  });

  it("renders an OpenAI help link when the tool is codex", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "openai", resolvable: false, source: null }),
    );
    renderPage();
    await advanceToExecution();
    await selectTool("codex");
    const link = await screen.findByTestId("credential-help-link");
    expect(link.getAttribute("href")).toBe(
      "https://platform.openai.com/api-keys",
    );
  });

  it("renders a Google AI help link when the tool is gemini", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "google", resolvable: false, source: null }),
    );
    renderPage();
    await advanceToExecution();
    await selectTool("gemini");
    const link = await screen.findByTestId("credential-help-link");
    expect(link.getAttribute("href")).toBe(
      "https://aistudio.google.com/app/apikey",
    );
  });
});

// Regression: when Step 2 disables Next, the wizard must always
// surface a human-readable reason.
describe("CreateUnitPage — Step 2 explains a disabled Next", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
  });

  it("warns when the agent-runtime catalog is empty and Claude Code is selected", async () => {
    listAgentRuntimes.mockResolvedValue([]);
    renderPage();
    await advanceToExecution();

    const banner = await screen.findByTestId("agent-runtime-catalog-issue");
    expect(banner.textContent).toMatch(/no configured agent runtimes/i);

    const next = screen.getByRole("button", { name: /^next$/i });
    expect(next).toBeDisabled();

    const reason = await screen.findByTestId("next-disabled-reason");
    expect(reason.textContent).toMatch(/no configured agent runtimes/i);
  });

  it("warns when the agent-runtime catalog fetch fails", async () => {
    listAgentRuntimes.mockRejectedValue(
      new Error("API error 502: Bad Gateway"),
    );
    renderPage();
    await advanceToExecution();

    const banner = await screen.findByTestId("agent-runtime-catalog-issue");
    expect(banner.textContent).toBe(
      "Could not load the agent-runtime catalog.",
    );

    const reason = await screen.findByTestId("next-disabled-reason");
    expect(reason.textContent).toBe(
      "Could not load the agent-runtime catalog.",
    );
  });

  it("explains the missing runtime when the selected tool's runtime isn't installed", async () => {
    listAgentRuntimes.mockResolvedValue([
      makeRuntime({
        id: "openai",
        displayName: "OpenAI",
        toolKind: "dapr-agent",
        models: ["gpt-4o"],
        defaultModel: "gpt-4o",
        credentialKind: "ApiKey",
      }),
    ]);
    renderPage();
    await advanceToExecution();

    const reason = await screen.findByTestId("next-disabled-reason");
    expect(reason.textContent).toMatch(
      /Claude Code.*runtime is not installed/i,
    );
    expect(screen.getByRole("button", { name: /^next$/i })).toBeDisabled();
  });
});

// #1033: the create-unit POST must always carry the wizard's resolved
// `tool` / `provider` — the previous "suppress when equals default"
// shortcut dropped `tool=claude-code` on the floor and landed the unit
// with `tool: (unset)`, which then broke every template-instantiated
// agent at first dispatch.
describe("CreateUnitPage — #1033 execution.tool propagation", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    createUnit.mockResolvedValue({ name: "acme", id: "acme-id" });
    startUnit.mockResolvedValue(undefined);
    getUnit.mockResolvedValue({
      id: "acme-id",
      name: "acme",
      displayName: "Acme",
      description: "",
      registeredAt: "2026-04-21T00:00:00Z",
      status: "Running",
      model: "claude-opus-4-7",
      color: null,
      tool: "claude-code",
      provider: "claude",
      hosting: null,
      lastValidationError: null,
      lastValidationRunId: null,
    });
    getUnitExecution.mockResolvedValue({
      unitId: "acme-id",
      image: null,
      runtime: null,
      model: null,
      secrets: null,
      updatedAt: null,
    });
  });

  async function advanceScratchToFinalize() {
    renderPage();
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "acme" } });
    });
    const clickNext = async () => {
      const next = screen.getByRole("button", { name: /^next$/i });
      await act(async () => {
        fireEvent.click(next);
      });
    };
    await clickNext(); // → Execution
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });
    await clickNext(); // → Mode
    const scratch = screen.getByRole("button", { name: /scratch/i });
    await act(async () => {
      fireEvent.click(scratch);
    });
    await clickNext(); // → Connector
    await clickNext(); // → Secrets
    await clickNext(); // → Finalize
  }

  it("sends tool='claude-code' on the create-unit body even when the default is unchanged", async () => {
    await advanceScratchToFinalize();
    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(createUnit).toHaveBeenCalledTimes(1);
    });
    const body = createUnit.mock.calls[0]?.[0] as Record<string, unknown>;
    // The wizard's default tool is 'claude-code' (#690 seeds it). Prior
    // to the #1033 fix the wizard suppressed this field because it
    // equalled DEFAULT_EXECUTION_TOOL — the unit then landed with
    // `tool: (unset)` and dispatch failed with SpringException from
    // A2AExecutionDispatcher. Now it must be present on the wire.
    expect(body.tool).toBe("claude-code");
    // The fixed-provider tools (claude-code / codex / gemini) must also
    // send the canonical provider so `IsFullyConfiguredForValidationAsync`
    // can transition the unit straight into Validating on create.
    expect(body.provider).toBe("claude");
  });

  it("sends tool and provider when creating from a template", async () => {
    listUnitTemplates.mockResolvedValue([
      {
        package: "software-engineering",
        name: "engineering-team",
        displayName: "Engineering team",
        description: "Coordinated software engineering team.",
        path: "packages/software-engineering/units/engineering-team.yaml",
      },
    ]);
    createUnitFromTemplate.mockResolvedValue({
      unit: { name: "portal-tpl-eng-1", id: "tpl-id" },
      warnings: [],
    });

    renderPage();
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "portal-tpl-eng-1" } });
    });

    const clickNext = async () => {
      const next = screen.getByRole("button", { name: /^next$/i });
      await act(async () => {
        fireEvent.click(next);
      });
    };
    await clickNext(); // → Execution
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });
    await clickNext(); // → Mode
    const templateBtn = screen.getByRole("button", { name: /^template/i });
    await act(async () => {
      fireEvent.click(templateBtn);
    });
    // Pick the only template on offer — the picker renders a button
    // whose text carries the "{package}/{name}" header.
    const templateRadio = await screen.findByRole("button", {
      name: /software-engineering\/engineering-team/i,
    });
    await act(async () => {
      fireEvent.click(templateRadio);
    });
    await clickNext(); // → Connector
    await clickNext(); // → Secrets
    await clickNext(); // → Finalize

    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(createUnitFromTemplate).toHaveBeenCalledTimes(1);
    });
    const body = createUnitFromTemplate.mock.calls[0]?.[0] as Record<
      string,
      unknown
    >;
    expect(body.tool).toBe("claude-code");
    expect(body.provider).toBe("claude");
    // #1033 + #325: the wizard also forwards the operator's name
    // override so repeated template instantiations don't collide on the
    // server's unique-name constraint.
    expect(body.unitName).toBe("portal-tpl-eng-1");
  });
});

// #1034: the Finalize summary's Name row lied about the name the unit
// would be created with when Mode = Template. Now the row always echoes
// the typed name back when one was supplied.
describe("CreateUnitPage — #1034 Finalize summary respects typed name", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
  });

  it("echoes the typed name in template mode instead of '(from template …)'", async () => {
    listUnitTemplates.mockResolvedValue([
      {
        package: "software-engineering",
        name: "engineering-team",
        displayName: "Engineering team",
        description: "Coordinated software engineering team.",
        path: "packages/software-engineering/units/engineering-team.yaml",
      },
    ]);

    renderPage();
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "portal-tpl-eng-1" } });
    });
    const clickNext = async () => {
      const next = screen.getByRole("button", { name: /^next$/i });
      await act(async () => {
        fireEvent.click(next);
      });
    };
    await clickNext(); // → Execution
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });
    await clickNext(); // → Mode
    const templateBtn = screen.getByRole("button", { name: /^template/i });
    await act(async () => {
      fireEvent.click(templateBtn);
    });
    const templateRadio = await screen.findByRole("button", {
      name: /software-engineering\/engineering-team/i,
    });
    await act(async () => {
      fireEvent.click(templateRadio);
    });
    await clickNext(); // → Connector
    await clickNext(); // → Secrets
    await clickNext(); // → Finalize

    // The Finalize summary must display the operator-supplied name —
    // not "(from template software-engineering/engineering-team)",
    // which was the bug.
    const finalize = await screen.findByText(/^Name$/i);
    const summaryRow = finalize.parentElement as HTMLElement | null;
    expect(summaryRow).not.toBeNull();
    expect(summaryRow!.textContent).toContain("portal-tpl-eng-1");
    expect(summaryRow!.textContent).not.toMatch(/from template/i);
  });

  // Complements the positive case above: `renderNameSummary` still
  // returns the template-scoped label when the operator truly left the
  // Name field blank. This is a direct test of the helper to avoid
  // recreating the full click path (Step 1 gates advance on a name, so
  // a UI path to a blank-name Finalize requires a typed-then-cleared
  // workaround that tests the wizard's rerender gymnastics more than
  // the summary logic).
  it("renderNameSummary echoes the typed name, falling back to the template/yaml label only when blank", async () => {
    const { renderNameSummary } = await import("./page");
    expect(
      renderNameSummary({
        name: "",
        mode: "template",
        templateId: "software-engineering/engineering-team",
      }),
    ).toBe("(from template software-engineering/engineering-team)");
    expect(
      renderNameSummary({
        name: "portal-tpl-eng-1",
        mode: "template",
        templateId: "software-engineering/engineering-team",
      }),
    ).toBe("portal-tpl-eng-1");
    expect(
      renderNameSummary({
        name: "  ",
        mode: "yaml",
        templateId: null,
      }),
    ).toBe("(from YAML manifest)");
    expect(
      renderNameSummary({
        name: "",
        mode: "scratch",
        templateId: null,
      }),
    ).toBe("—");
  });
});
