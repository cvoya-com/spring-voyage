import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
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
const deleteUnit = vi.fn();
const revalidateUnit = vi.fn();
const getUnit = vi.fn();
const getUnitExecution = vi.fn();
const setUnitExecution = vi.fn();
const getTenantTree = vi.fn();

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
    deleteUnit: (name: string) => deleteUnit(name),
    revalidateUnit: (name: string) => revalidateUnit(name),
    getUnit: (name: string) => getUnit(name),
    getUnitExecution: (name: string) => getUnitExecution(name),
    setUnitExecution: (name: string, body: unknown) =>
      setUnitExecution(name, body),
    getTenantTree: () => getTenantTree(),
  },
}));

// The Finalize step mounts ValidationPanel, which subscribes to
// `useActivityStream`. Stub it so the test doesn't try to open a real
// EventSource under JSDOM.
vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: true }),
}));

// #622 / #968: mock the image-history module so tests can control the
// history store without depending on localStorage (which is not fully
// available in JSDOM).
const mockLoadImageHistory = vi.fn<() => string[]>(() => []);
const mockRecordImageReference = vi.fn<(ref: string) => void>();
vi.mock("@/lib/image-history", () => ({
  loadImageHistory: () => mockLoadImageHistory(),
  recordImageReference: (ref: string) => mockRecordImageReference(ref),
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
  const id = overrides.id ?? "claude";
  const defaultImageForId = (runtimeId: string) => {
    switch (runtimeId) {
      case "claude":
        return "ghcr.io/cvoya-com/spring-voyage-agent-claude-code:latest";
      case "openai":
        return "ghcr.io/cvoya-com/spring-voyage-agent-codex:latest";
      case "google":
        return "ghcr.io/cvoya-com/spring-voyage-agent-google:latest";
      case "ollama":
        return "ghcr.io/cvoya-com/spring-voyage-agent-ollama:latest";
      default:
        return "ghcr.io/cvoya-com/spring-voyage-agent-base:latest";
    }
  };
  return {
    id,
    displayName: "Claude",
    toolKind: "claude-code-cli",
    installedAt: now,
    updatedAt: now,
    models: ["claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5"],
    defaultModel: "claude-opus-4-7",
    baseUrl: null,
    credentialKind: "ApiKey",
    credentialDisplayHint: null,
    credentialSecretName: "anthropic-api-key",
    defaultImage: defaultImageForId(id),
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
  // #814: step 1 now requires an explicit parent choice. Pick "Top-level"
  // so existing tests can advance without choosing "Has parent units".
  await act(async () => {
    fireEvent.click(screen.getByTestId("parent-choice-top-level"));
  });
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

/** #814: click the "Top-level" radio button on step 1. */
async function selectTopLevel() {
  await act(async () => {
    fireEvent.click(screen.getByTestId("parent-choice-top-level"));
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
  deleteUnit.mockResolvedValue(undefined);
  revalidateUnit.mockResolvedValue(undefined);
  setUnitExecution.mockResolvedValue(undefined);
  // #814: default to an empty tenant tree so the parent-unit picker
  // renders "No existing units" without failing. Tests that exercise
  // the picker override this.
  getTenantTree.mockResolvedValue({
    tree: { id: "tenant", name: "tenant", kind: "Tenant", status: "running" },
  });
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

  // Issue #1072: with dapr-agent + ollama selected, the wizard's Next
  // button stayed disabled because the model field was never seeded
  // from the live Ollama catalog. The dropdown rendered the first
  // option visually (the controlled <select> picks option[0] when
  // `value=""`) but `form.model` stayed "" and `modelIsSelected`
  // returned false. The fix auto-seeds `form.model` from the live
  // Ollama list, mirroring how the catalog-driven branch works.
  it("auto-seeds the model when dapr-agent + ollama is selected (#1072)", async () => {
    listOllamaModels.mockResolvedValue([{ name: "llama3.2:3b" }]);

    renderPage();
    await advanceToExecution();
    await selectTool("dapr-agent");

    const providerSelect = screen.getByLabelText(
      /^LLM provider$/i,
    ) as HTMLSelectElement;
    await act(async () => {
      fireEvent.change(providerSelect, { target: { value: "ollama" } });
    });

    const modelSelect = (await screen.findByLabelText(
      /^Model$/i,
    )) as HTMLSelectElement;
    await waitFor(() => {
      expect(modelSelect.value).toBe("llama3.2:3b");
    });

    const next = screen.getByRole("button", { name: /^next$/i });
    expect(next).not.toBeDisabled();
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
    await selectTopLevel(); // #814: required parent choice
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
    await selectTopLevel(); // #814
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
    await selectTopLevel(); // #814
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
    await selectTopLevel(); // #814
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
    // The mocked GET /units/{id} reports `Running` immediately, so the
    // wizard skips POST /start (the unit is already past Draft) and
    // redirects straight to the Explorer once the terminal status is
    // observed. The /start gate was tightened to avoid emitting a
    // spurious 409 when the create endpoint has already advanced the
    // unit's lifecycle (see "skips POST /start" test below).
    expect(startUnit).not.toHaveBeenCalled();
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
    await selectTopLevel(); // #814: required parent-choice gate
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

  it("success path: post-create status is Running, redirects without re-calling /start", async () => {
    // The create endpoint may advance the unit's lifecycle on its own
    // (it transitions to Validating when the unit is fully configured,
    // and the validation workflow then moves it to Stopped/Running).
    // The wizard observes the resulting status and only POSTs /start
    // when the unit is still in Draft — re-calling /start from a
    // non-Draft state is a 409 in the API.
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
    expect(startUnit).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith(
        "/units?node=acme&tab=Overview",
      );
    });
  });

  it("legacy Draft path: POSTs /revalidate when post-create status is still Draft", async () => {
    // First poll returns Draft (legacy scratch path: the create
    // endpoint didn't advance the lifecycle), subsequent polls flip
    // to Running so the redirect can fire.
    getUnit
      .mockResolvedValueOnce({
        id: "acme-id",
        name: "acme",
        displayName: "Acme",
        description: "",
        registeredAt: "2026-04-21T00:00:00Z",
        status: "Draft",
        model: "claude-opus-4-7",
        color: null,
        tool: "claude-code",
        provider: null,
        hosting: null,
        lastValidationError: null,
        lastValidationRunId: null,
      })
      .mockResolvedValue({
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

    // #1451: the wizard now calls revalidateUnit (Draft → Validating)
    // instead of startUnit (Draft → Starting was rejected by the
    // actor's transition table, #939).
    await waitFor(() => {
      expect(revalidateUnit).toHaveBeenCalledWith("acme");
    });
    expect(startUnit).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith(
        "/units?node=acme&tab=Overview",
      );
    });
  });

  it("error path: terminal Error keeps the user on Finalize with Back and Cancel affordances", async () => {
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

    // Backend already moved the unit past Draft (straight to Error in
    // this scenario), so the wizard does NOT call /start.
    await waitFor(() => {
      expect(createUnit).toHaveBeenCalledTimes(1);
    });
    expect(startUnit).not.toHaveBeenCalled();

    // Error action row shows up; redirect must NOT happen.
    await screen.findByTestId("wizard-validation-error-actions");
    expect(pushMock).not.toHaveBeenCalled();

    // Both Back and Cancel-and-delete must be present.
    expect(
      screen.getByTestId("wizard-validation-error-cancel"),
    ).toBeInTheDocument();

    // Back affordance steps the wizard back to Execution (step 2).
    const back = screen.getByTestId("wizard-validation-back");
    await act(async () => {
      fireEvent.click(back);
    });

    // Once the user steps back, the Execution step's Tool select is
    // visible again.
    expect(screen.getByLabelText("Execution tool")).toBeInTheDocument();
  });

  it("Cancel during Validating deletes the partial unit and routes back to /units", async () => {
    // Stay in Validating indefinitely so the Cancel surface stays mounted.
    getUnit.mockResolvedValue({
      id: "acme-id",
      name: "acme",
      displayName: "Acme",
      description: "",
      registeredAt: "2026-04-21T00:00:00Z",
      status: "Validating",
      model: "claude-opus-4-7",
      color: null,
      tool: "claude-code",
      provider: null,
      hosting: null,
      lastValidationError: null,
      lastValidationRunId: "run-pending",
    });

    await advanceWizardToFinalize();

    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    // Wait for the inline Cancel button to appear next to the
    // "Waiting for validation" status text.
    const cancel = await screen.findByTestId("wizard-validation-cancel");
    await act(async () => {
      fireEvent.click(cancel);
    });

    await waitFor(() => {
      expect(deleteUnit).toHaveBeenCalledWith("acme");
    });
    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith("/units");
    });
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
    // #814: pick top-level to satisfy the required parent-choice gate.
    await act(async () => {
      fireEvent.click(screen.getByTestId("parent-choice-top-level"));
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
    await selectTopLevel(); // #814
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
    await selectTopLevel(); // #814
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

// #1132: wizard state persistence across page reloads. Rehydrating
// from a sessionStorage snapshot must restore the operator at the right
// step with the right field values (excluding secrets), while a
// malformed snapshot must be discarded silently and the wizard must
// start at step 1. Both behaviours need explicit tests because the
// effect-driven save and the lazy-init load are easy to break in a
// future refactor.
describe("CreateUnitPage — #1132 wizard state persistence", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
  });

  function seedSnapshot(snapshot: unknown) {
    const runId = "test-run-1132";
    sessionStorage.setItem("spring.wizard.unit-create.run-id", runId);
    sessionStorage.setItem(
      `spring.wizard.unit-create.${runId}`,
      typeof snapshot === "string" ? snapshot : JSON.stringify(snapshot),
    );
  }

  it("rehydrates the wizard at the saved step with the saved field values", async () => {
    seedSnapshot({
      schemaVersion: 2,
      currentStep: 3,
      form: {
        name: "rehydrated-unit",
        displayName: "Rehydrated Unit",
        description: "Came back from a refresh.",
        provider: "claude",
        model: "claude-sonnet-4-6",
        color: "#abcdef",
        tool: "claude-code",
        hosting: "default",
        image: "",
        runtime: "",
        mode: "scratch",
        templateId: null,
        yamlText: "",
        yamlFileName: null,
        connectorSlug: null,
        connectorTypeId: null,
        connectorConfig: null,
        parentUnitId: null,
        parentChoice: "top-level",
        parentUnitIds: [],
      },
    });

    renderPage();

    // Step 3 (Mode) shows the mode cards. Without rehydrate the wizard
    // would mount at step 1 and the mode picker wouldn't be visible at
    // all.
    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /scratch/i }),
      ).toBeInTheDocument();
    });
    expect(
      screen.getByRole("button", { name: /^template/i }),
    ).toBeInTheDocument();

    // Stepping back to Identity (step 1) should show the persisted
    // name + display name in the controls — proving the form snapshot
    // landed in component state.
    const back = screen.getByRole("button", { name: /^back$/i });
    await act(async () => {
      fireEvent.click(back);
    });
    await act(async () => {
      fireEvent.click(back);
    });
    await waitFor(() => {
      const nameInput = screen.getByPlaceholderText(
        /engineering-team/i,
      ) as HTMLInputElement;
      expect(nameInput.value).toBe("rehydrated-unit");
    });
    const displayNameInput = screen.getByPlaceholderText(
      /Engineering Team/i,
    ) as HTMLInputElement;
    expect(displayNameInput.value).toBe("Rehydrated Unit");
  });

  it("discards a snapshot whose schema doesn't validate and starts at step 1", async () => {
    // Stale schema version (a future major bump must drop pre-bump
    // blobs on the floor). The wizard must not crash and must mount
    // at step 1 with an empty Name field.
    seedSnapshot({
      schemaVersion: 999,
      currentStep: 4,
      form: {
        name: "should-not-rehydrate",
      },
    });

    renderPage();

    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    expect(nameInput.value).toBe("");
    // Step 1 (Identity) renders the Name + Display name controls. Any
    // higher step would not render those in this layout; finding both
    // confirms we mounted at step 1.
    expect(
      screen.getByPlaceholderText(/Engineering Team/i),
    ).toBeInTheDocument();
  });

  it("discards a snapshot whose JSON is malformed", async () => {
    seedSnapshot("not-json{");

    renderPage();

    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    expect(nameInput.value).toBe("");
  });

  it("clears the snapshot when the operator clicks Cancel", async () => {
    renderPage();
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "abandoned" } });
    });

    // Wait for the debounced save to settle. The save effect uses
    // setTimeout(300ms); a generous wait keeps the test stable.
    await new Promise((r) => setTimeout(r, 400));

    const runId = sessionStorage.getItem("spring.wizard.unit-create.run-id");
    expect(runId).not.toBeNull();
    expect(
      sessionStorage.getItem(`spring.wizard.unit-create.${runId}`),
    ).not.toBeNull();

    const cancel = screen.getByTestId("wizard-cancel");
    await act(async () => {
      fireEvent.click(cancel);
    });

    expect(
      sessionStorage.getItem("spring.wizard.unit-create.run-id"),
    ).toBeNull();
    expect(
      sessionStorage.getItem(`spring.wizard.unit-create.${runId}`),
    ).toBeNull();
    expect(pushMock).toHaveBeenCalledWith("/units");
  });
});

// #1150: the wizard accepts an optional `?parent=<unitId>` query
// param and threads it through `parentUnitIds` / `isTopLevel: false`
// on every create-unit endpoint. The Identity step surfaces a banner
// naming the parent so the operator knows what they're nesting under,
// and exposes a Clear affordance that drops back to top-level
// creation. Top-level creation (no `?parent=`) must keep working
// unchanged.
describe("CreateUnitPage — #1150 sub-unit creation", () => {
  const ORIGINAL_LOCATION = window.location;

  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
    createUnit.mockResolvedValue({ name: "acme-child", id: "acme-child-id" });
    startUnit.mockResolvedValue(undefined);
    // The wizard polls the just-created unit AND fetches the parent
    // unit envelope (for the banner). Route the mock by id so both
    // queries get sensible payloads.
    getUnit.mockImplementation(async (id: string) => {
      if (id === "engineering-team") {
        return {
          id: "engineering-team",
          name: "engineering-team",
          displayName: "Engineering Team",
          description: "Parent unit fixture for #1150 tests.",
          registeredAt: "2026-04-21T00:00:00Z",
          status: "Running",
          model: null,
          color: null,
          tool: null,
          provider: null,
          hosting: null,
          lastValidationError: null,
          lastValidationRunId: null,
        };
      }
      return {
        id: "acme-child-id",
        name: "acme-child",
        displayName: "Acme child",
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
      };
    });
    getUnitExecution.mockResolvedValue({
      unitId: "acme-child-id",
      image: null,
      runtime: null,
      model: null,
      secrets: null,
      updatedAt: null,
    });
  });

  afterEachRestoreLocation();

  function setSearch(search: string) {
    // jsdom forbids assigning to window.location directly, but the
    // wizard reads `window.location.search` via `URLSearchParams` in
    // a lazy useState initialiser, so substituting the property is
    // enough.
    Object.defineProperty(window, "location", {
      configurable: true,
      writable: true,
      value: { ...ORIGINAL_LOCATION, search },
    });
  }

  function afterEachRestoreLocation() {
    // We register a cleanup that runs after every test in this
    // describe block. Defining it inside the describe keeps the
    // restore tied to the same suite that overrides the location.
    afterEach(() => {
      Object.defineProperty(window, "location", {
        configurable: true,
        writable: true,
        value: ORIGINAL_LOCATION,
      });
    });
  }

  async function fillNameAndAdvance() {
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    if (nameInput.value === "") {
      await act(async () => {
        fireEvent.change(nameInput, { target: { value: "acme-child" } });
      });
    }
    // #814: pick top-level if neither choice is already selected.
    // When ?parent= is set, parentChoice is pre-seeded to "has-parents".
    const topLevelBtn = screen.queryByTestId("parent-choice-top-level");
    if (
      topLevelBtn &&
      topLevelBtn.getAttribute("aria-checked") !== "true" &&
      screen
        .getByTestId("parent-choice-has-parents")
        .getAttribute("aria-checked") !== "true"
    ) {
      await act(async () => {
        fireEvent.click(topLevelBtn);
      });
    }
    const next = screen.getByRole("button", { name: /^next$/i });
    await act(async () => {
      fireEvent.click(next);
    });
  }

  async function advanceScratchToFinalize() {
    await fillNameAndAdvance(); // Identity → Execution
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });
    const next = () => screen.getByRole("button", { name: /^next$/i });
    await act(async () => {
      fireEvent.click(next());
    });
    const scratch = screen.getByRole("button", { name: /scratch/i });
    await act(async () => {
      fireEvent.click(scratch);
    });
    await act(async () => {
      fireEvent.click(next());
    });
    await act(async () => {
      fireEvent.click(next());
    });
    await act(async () => {
      fireEvent.click(next());
    });
  }

  it("seeds has-parents choice from URL param and routes the create-unit body through parentUnitIds", async () => {
    setSearch("?parent=engineering-team");

    renderPage();

    // #814: the picker pre-selects "Has parent units" from the URL param.
    // The heading reskins to reflect the sub-unit intent.
    expect(
      screen.getByRole("heading", { name: /create a sub-unit/i }),
    ).toBeInTheDocument();
    // The parent-unit picker is visible.
    expect(await screen.findByTestId("parent-unit-picker")).toBeInTheDocument();
    // The "Has parent units" radio is checked.
    expect(
      screen.getByTestId("parent-choice-has-parents").getAttribute("aria-checked"),
    ).toBe("true");
    // The selected parent's display name is shown once the query resolves.
    await waitFor(() => {
      expect(screen.getByText(/engineering team/i)).toBeInTheDocument();
    });

    await advanceScratchToFinalize();

    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(createUnit).toHaveBeenCalledTimes(1);
    });
    const body = createUnit.mock.calls[0]?.[0] as Record<string, unknown>;
    expect(body.parentUnitIds).toEqual(["engineering-team"]);
    expect(body.isTopLevel).toBe(false);
  });

  it("switching to Top-level after URL-param seed reverts to top-level creation", async () => {
    setSearch("?parent=engineering-team");

    renderPage();

    // Switch to top-level.
    const topLevel = await screen.findByTestId("parent-choice-top-level");
    await act(async () => {
      fireEvent.click(topLevel);
    });

    // Heading reverts to plain "Create a unit".
    expect(
      screen.getByRole("heading", { name: /^create a unit$/i }),
    ).toBeInTheDocument();
    // The banner is gone (no parentUnitMissing on top-level).
    expect(
      screen.queryByTestId("parent-unit-banner"),
    ).not.toBeInTheDocument();

    await advanceScratchToFinalize();
    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(createUnit).toHaveBeenCalledTimes(1);
    });
    const body = createUnit.mock.calls[0]?.[0] as Record<string, unknown>;
    // Explicit top-level: isTopLevel is true, no parentUnitIds.
    expect(body.isTopLevel).toBe(true);
    expect(body.parentUnitIds).toBeUndefined();
  });

  it("legacy path: no `?parent=` keeps the existing top-level flow when top-level is chosen", async () => {
    // No setSearch — defaults to whatever JSDOM's default location is.
    renderPage();

    expect(
      screen.queryByTestId("parent-unit-banner"),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: /^create a unit$/i }),
    ).toBeInTheDocument();

    await advanceScratchToFinalize();
    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(createUnit).toHaveBeenCalledTimes(1);
    });
    const body = createUnit.mock.calls[0]?.[0] as Record<string, unknown>;
    // advanceScratchToFinalize uses fillNameAndAdvance which picks top-level.
    // Explicit top-level: isTopLevel is true, no parentUnitIds.
    expect(body.isTopLevel).toBe(true);
  });
});

// #814: parent-unit picker — explicit top-level vs has-parents choice.
describe("CreateUnitPage — #814 parent-unit picker", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
  });

  it("blocks Next on step 1 when no parent choice is made", async () => {
    renderPage();

    // Fill the name but do NOT pick top-level or has-parents.
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "blocked" } });
    });

    const next = screen.getByRole("button", { name: /^next$/i });
    await act(async () => {
      fireEvent.click(next);
    });

    // Step 1 error is shown.
    await waitFor(() => {
      expect(
        screen.getByText(/choose whether this unit is top-level/i),
      ).toBeInTheDocument();
    });
    // Still on step 1.
    expect(screen.getByPlaceholderText(/engineering-team/i)).toBeInTheDocument();
  });

  it("blocks Next when has-parents is chosen but no unit is selected", async () => {
    renderPage();

    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "blocked" } });
    });

    await act(async () => {
      fireEvent.click(screen.getByTestId("parent-choice-has-parents"));
    });

    const next = screen.getByRole("button", { name: /^next$/i });
    await act(async () => {
      fireEvent.click(next);
    });

    await waitFor(() => {
      expect(
        screen.getByText(/select at least one parent unit/i),
      ).toBeInTheDocument();
    });
  });

  it("shows the picker with available units when has-parents is chosen", async () => {
    getTenantTree.mockResolvedValue({
      tree: {
        id: "tenant",
        name: "tenant",
        kind: "Tenant",
        status: "running",
        children: [
          {
            id: "eng-unit-id",
            name: "engineering",
            kind: "Unit",
            status: "running",
          },
          {
            id: "product-unit-id",
            name: "product",
            kind: "Unit",
            status: "running",
          },
        ],
      },
    });

    renderPage();

    await act(async () => {
      fireEvent.click(screen.getByTestId("parent-choice-has-parents"));
    });

    const picker = await screen.findByTestId("parent-unit-picker");
    expect(picker).toBeInTheDocument();
    // Both units are listed.
    expect(
      await screen.findByTestId("parent-option-eng-unit-id"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("parent-option-product-unit-id"),
    ).toBeInTheDocument();
  });

  it("sends parentUnitIds when a parent unit is selected via the picker", async () => {
    getTenantTree.mockResolvedValue({
      tree: {
        id: "tenant",
        name: "tenant",
        kind: "Tenant",
        status: "running",
        children: [
          {
            id: "eng-unit-id",
            name: "engineering",
            kind: "Unit",
            status: "running",
          },
        ],
      },
    });
    createUnit.mockResolvedValue({ name: "child", id: "child-id" });
    startUnit.mockResolvedValue(undefined);
    getUnit.mockResolvedValue({
      id: "child-id",
      name: "child",
      displayName: "Child",
      description: "",
      registeredAt: new Date().toISOString(),
      status: "Running",
      model: null,
      color: null,
      tool: null,
      provider: null,
      hosting: null,
      lastValidationError: null,
      lastValidationRunId: null,
    });
    getUnitExecution.mockResolvedValue({
      unitId: "child-id",
      image: null,
      runtime: null,
      model: null,
      secrets: null,
      updatedAt: null,
    });

    renderPage();

    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "child" } });
    });

    // Pick "Has parent units".
    await act(async () => {
      fireEvent.click(screen.getByTestId("parent-choice-has-parents"));
    });

    // Select the engineering unit.
    const engOption = await screen.findByTestId("parent-option-eng-unit-id");
    await act(async () => {
      fireEvent.click(engOption);
    });

    // Advance through all steps.
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

    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(createUnit).toHaveBeenCalledTimes(1);
    });
    const body = createUnit.mock.calls[0]?.[0] as Record<string, unknown>;
    expect(body.parentUnitIds).toEqual(["eng-unit-id"]);
    expect(body.isTopLevel).toBe(false);
  });

  it("top-level choice sends isTopLevel:true", async () => {
    createUnit.mockResolvedValue({ name: "top", id: "top-id" });
    startUnit.mockResolvedValue(undefined);
    getUnit.mockResolvedValue({
      id: "top-id",
      name: "top",
      displayName: "Top",
      description: "",
      registeredAt: new Date().toISOString(),
      status: "Running",
      model: null,
      color: null,
      tool: null,
      provider: null,
      hosting: null,
      lastValidationError: null,
      lastValidationRunId: null,
    });
    getUnitExecution.mockResolvedValue({
      unitId: "top-id",
      image: null,
      runtime: null,
      model: null,
      secrets: null,
      updatedAt: null,
    });

    renderPage();

    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "top" } });
    });

    await act(async () => {
      fireEvent.click(screen.getByTestId("parent-choice-top-level"));
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

    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(createUnit).toHaveBeenCalledTimes(1);
    });
    const body = createUnit.mock.calls[0]?.[0] as Record<string, unknown>;
    expect(body.isTopLevel).toBe(true);
    expect(body.parentUnitIds).toBeUndefined();
  });
});

// #968 / #622: image input suggestions (recently-used image history).
describe("CreateUnitPage — #968/#622 image-reference suggestions", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
    // mockLoadImageHistory defaults to returning [] (set at declaration).
    // Reset it explicitly so each test starts clean.
    mockLoadImageHistory.mockReturnValue([]);
  });

  it("shows no datalist when history is empty", async () => {
    mockLoadImageHistory.mockReturnValue([]);
    renderPage();
    await advanceToExecution();

    // The image input exists but has no list attribute when history is empty.
    const imageInput = screen.getByLabelText(/^execution image$/i);
    expect(imageInput.getAttribute("list")).toBeNull();
    expect(
      document.getElementById("image-history-suggestions"),
    ).toBeNull();
  });

  it("shows datalist suggestions when history has prior image refs", async () => {
    mockLoadImageHistory.mockReturnValue([
      "ghcr.io/spring-voyage/agent:latest",
      "localhost/spring-agent:dev",
    ]);

    renderPage();
    await advanceToExecution();

    // The datalist element exists with the persisted suggestions.
    const datalist = document.getElementById("image-history-suggestions");
    expect(datalist).not.toBeNull();
    const options = datalist!.querySelectorAll("option");
    const values = Array.from(options).map((o) => o.value);
    expect(values).toContain("ghcr.io/spring-voyage/agent:latest");
    expect(values).toContain("localhost/spring-agent:dev");

    // The image input is wired to the datalist.
    const imageInput = screen.getByLabelText(/^execution image$/i);
    expect(imageInput.getAttribute("list")).toBe("image-history-suggestions");
  });

  it("calls recordImageReference after successful unit creation", async () => {
    createUnit.mockResolvedValue({ name: "img-test", id: "img-test-id" });
    startUnit.mockResolvedValue(undefined);
    getUnit.mockResolvedValue({
      id: "img-test-id",
      name: "img-test",
      displayName: "Img Test",
      description: "",
      registeredAt: new Date().toISOString(),
      status: "Running",
      model: null,
      color: null,
      tool: null,
      provider: null,
      hosting: null,
      lastValidationError: null,
      lastValidationRunId: null,
    });
    getUnitExecution.mockResolvedValue({
      unitId: "img-test-id",
      image: "ghcr.io/spring-voyage/agent:latest",
      runtime: null,
      model: null,
      secrets: null,
      updatedAt: null,
    });

    renderPage();

    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "img-test" } });
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId("parent-choice-top-level"));
    });
    const clickNext = async () => {
      const next = screen.getByRole("button", { name: /^next$/i });
      await act(async () => {
        fireEvent.click(next);
      });
    };
    await clickNext(); // → Execution

    // Fill in an image reference.
    const imageInput = screen.getByLabelText(/^execution image$/i);
    await act(async () => {
      fireEvent.change(imageInput, {
        target: { value: "ghcr.io/spring-voyage/agent:v1.0" },
      });
    });

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

    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(createUnit).toHaveBeenCalledTimes(1);
    });

    // recordImageReference must have been called with the submitted image.
    await waitFor(() => {
      expect(mockRecordImageReference).toHaveBeenCalledWith(
        "ghcr.io/spring-voyage/agent:v1.0",
      );
    });
  });
});

// ---------------------------------------------------------------------------
// #1509: categorizeWarning — pure unit tests
// ---------------------------------------------------------------------------
describe("categorizeWarning", () => {
  it("classifies a section-not-applied warning", async () => {
    const { categorizeWarning } = await import("./page");
    const result = categorizeWarning(
      "section 'ai' is parsed but not yet applied",
    );
    expect(result).toEqual({
      kind: "section-not-applied",
      section: "ai",
      raw: "section 'ai' is parsed but not yet applied",
    });
  });

  it("classifies a tool-not-surfaced warning", async () => {
    const { categorizeWarning } = await import("./page");
    const result = categorizeWarning(
      "bundle 'spring-voyage/software-engineering/triage-and-assign' requires tool 'assignToAgent', which is not surfaced by any registered connector; the agent may get a 'tool not found' error if it tries to call it.",
    );
    expect(result).toEqual({
      kind: "tool-not-surfaced",
      bundle: "spring-voyage/software-engineering/triage-and-assign",
      tool: "assignToAgent",
      raw: "bundle 'spring-voyage/software-engineering/triage-and-assign' requires tool 'assignToAgent', which is not surfaced by any registered connector; the agent may get a 'tool not found' error if it tries to call it.",
    });
  });

  it("classifies an unrecognised warning as unknown", async () => {
    const { categorizeWarning } = await import("./page");
    const result = categorizeWarning("something completely unexpected");
    expect(result).toEqual({
      kind: "unknown",
      raw: "something completely unexpected",
    });
  });

  it("is case-insensitive for the section pattern", async () => {
    const { categorizeWarning } = await import("./page");
    const result = categorizeWarning(
      "Section 'Connectors' is Parsed But Not Yet Applied",
    );
    expect(result.kind).toBe("section-not-applied");
    if (result.kind === "section-not-applied") {
      expect(result.section).toBe("Connectors");
    }
  });
});

// ---------------------------------------------------------------------------
// #1509: SubmitWarningsPanel rendering — integration via the wizard
// (using the template path, which returns warnings from the server response)
// ---------------------------------------------------------------------------
describe("CreateUnitPage — #1509 submit warnings panel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    // Seed a single template so Mode step can select it.
    listUnitTemplates.mockResolvedValue([
      {
        package: "software-engineering",
        name: "engineering-team",
        displayName: "Engineering team",
        description: "Coordinated software engineering team.",
        path: "packages/software-engineering/units/engineering-team.yaml",
      },
    ]);
  });

  afterEach(() => {
    sessionStorage.clear();
  });

  /**
   * Drives the wizard through all steps using the template path and clicks
   * Create. The mock returns the given warnings on the template response.
   */
  async function driveWizardToCreate(mockWarnings: string[]): Promise<void> {
    createUnitFromTemplate.mockResolvedValue({
      unit: { name: "portal-warn-test", id: "warn-id" },
      warnings: mockWarnings,
    });

    renderPage();
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "portal-warn-test" } });
    });

    const clickNext = async () => {
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: /^next$/i }));
      });
    };

    await act(async () => {
      fireEvent.click(screen.getByTestId("parent-choice-top-level"));
    });
    await clickNext(); // → Execution
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });
    await clickNext(); // → Mode

    // Select the Template mode card, then pick the template.
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

    await act(async () => {
      fireEvent.click(screen.getByTestId("create-unit-button"));
    });
    await waitFor(() => expect(createUnitFromTemplate).toHaveBeenCalledTimes(1));
  }

  it("uses info/blue styling and 'notices' label when all warnings are informational", async () => {
    await driveWizardToCreate([
      "section 'ai' is parsed but not yet applied",
      "section 'connectors' is parsed but not yet applied",
      "bundle 'spring-voyage/software-engineering/triage' requires tool 'assignToAgent', which is not surfaced by any registered connector",
    ]);

    await waitFor(() => {
      const panel = screen.getByTestId("submit-warnings-panel");
      // Info tone: role="status" not role="alert"
      expect(panel.getAttribute("role")).toBe("status");
      expect(panel.className).toContain("border-primary/40");
      // Title contains "notices" not "warnings"
      expect(panel.textContent).toMatch(/3 notices/i);
    });
  });

  it("uses amber styling and 'warnings' label when any warning is unknown", async () => {
    await driveWizardToCreate([
      "section 'ai' is parsed but not yet applied",
      "completely unrecognised warning text from the server",
    ]);

    await waitFor(() => {
      const panel = screen.getByTestId("submit-warnings-panel");
      expect(panel.getAttribute("role")).toBe("alert");
      expect(panel.className).toContain("amber-500");
      expect(panel.textContent).toMatch(/2 warnings/i);
    });
  });

  it("default-collapses the panel when all warnings are informational", async () => {
    await driveWizardToCreate([
      "section 'nodes' is parsed but not yet applied",
    ]);

    await waitFor(() => {
      const toggleBtn = screen
        .getByTestId("submit-warnings-panel")
        .querySelector("button[aria-expanded]");
      expect(toggleBtn?.getAttribute("aria-expanded")).toBe("false");
    });
  });

  it("default-expands the panel when any warning is unknown", async () => {
    await driveWizardToCreate(["something completely unexpected from the server"]);

    await waitFor(() => {
      const toggleBtn = screen
        .getByTestId("submit-warnings-panel")
        .querySelector("button[aria-expanded]");
      expect(toggleBtn?.getAttribute("aria-expanded")).toBe("true");
    });
  });

  it("shows section names after expanding an all-informational panel", async () => {
    await driveWizardToCreate([
      "section 'ai' is parsed but not yet applied",
      "section 'humans' is parsed but not yet applied",
    ]);

    // Panel starts collapsed for all-informational — expand it.
    await waitFor(async () => {
      const toggleBtn = screen
        .getByTestId("submit-warnings-panel")
        .querySelector("button[aria-expanded]");
      expect(toggleBtn).toBeTruthy();
      await act(async () => {
        fireEvent.click(toggleBtn!);
      });
    });

    await waitFor(() => {
      const panel = screen.getByTestId("submit-warnings-panel");
      expect(panel.textContent).toContain("ai");
      expect(panel.textContent).toContain("humans");
    });
  });

  it("shows tool names with a GitHub hint for software-engineering bundles", async () => {
    await driveWizardToCreate([
      "bundle 'spring-voyage/software-engineering/pr-review' requires tool 'requestReview', which is not surfaced by any registered connector",
    ]);

    // Panel starts collapsed — expand it.
    await waitFor(async () => {
      const toggleBtn = screen
        .getByTestId("submit-warnings-panel")
        .querySelector("button[aria-expanded]");
      expect(toggleBtn).toBeTruthy();
      await act(async () => {
        fireEvent.click(toggleBtn!);
      });
    });

    await waitFor(() => {
      const panel = screen.getByTestId("submit-warnings-panel");
      expect(panel.textContent).toContain("requestReview");
      expect(panel.textContent).toContain("GitHub");
    });
  });
});
