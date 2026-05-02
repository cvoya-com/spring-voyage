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
  InstallStatusResponse,
  ProviderCredentialStatusResponse,
} from "@/lib/api/types";

// ADR-0035 (#1563): the wizard now routes unit creation through the
// package install API. The old createUnit / createUnitFromTemplate /
// createUnitFromYaml paths are replaced by installPackages (catalog) and
// installPackageFile (scratch). Tests for the old paths have been
// removed; new tests cover the new install + polling flow.
const listOllamaModels = vi.fn();
const listAgentRuntimes = vi.fn();
const getAgentRuntimeModels = vi.fn();
const getProviderCredentialStatus = vi.fn();
const installPackages = vi.fn();
const installPackageFile = vi.fn();
const getInstallStatus = vi.fn();
const retryInstall = vi.fn();
const abortInstall = vi.fn();
const listPackages = vi.fn();
const createTenantSecret = vi.fn();
const rotateTenantSecret = vi.fn();
const deleteUnit = vi.fn();
const revalidateUnit = vi.fn();
const getUnit = vi.fn();
const getUnitExecution = vi.fn();
const setUnitExecution = vi.fn();
const getTenantTree = vi.fn();

const listConnectorTypes = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    listOllamaModels: () => listOllamaModels(),
    listAgentRuntimes: () => listAgentRuntimes(),
    getAgentRuntimeModels: (id: string) => getAgentRuntimeModels(id),
    getProviderCredentialStatus: (p: string) => getProviderCredentialStatus(p),
    getConnectorTypes: vi.fn().mockResolvedValue([]),
    listConnectorTypes: () => listConnectorTypes(),
    // ADR-0035 install API.
    installPackages: (targets: unknown) => installPackages(targets),
    installPackageFile: (yaml: string) => installPackageFile(yaml),
    getInstallStatus: (id: string) => getInstallStatus(id),
    retryInstall: (id: string) => retryInstall(id),
    abortInstall: (id: string) => abortInstall(id),
    listPackages: () => listPackages(),
    createTenantSecret: (body: unknown) => createTenantSecret(body),
    rotateTenantSecret: (name: string, body: unknown) =>
      rotateTenantSecret(name, body),
    deleteUnit: (name: string) => deleteUnit(name),
    revalidateUnit: (name: string) => revalidateUnit(name),
    getUnit: (name: string) => getUnit(name),
    getUnitExecution: (name: string) => getUnitExecution(name),
    setUnitExecution: (name: string, body: unknown) =>
      setUnitExecution(name, body),
    getTenantTree: () => getTenantTree(),
  },
}));

// The ValidationPanel in the old Finalize step subscribed to
// useActivityStream. Keep the stub to avoid real EventSource under JSDOM
// in case any residual imports pull it in.
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

/**
 * New flow: step 1 is Source. Select "Scratch", then click Next.
 * Returns to Identity (step 2 scratch).
 */
async function selectScratchSource() {
  const scratchCard = screen.getByTestId("source-card-scratch");
  await act(async () => {
    fireEvent.click(scratchCard);
  });
  const next = screen.getByRole("button", { name: /^next$/i });
  await act(async () => {
    fireEvent.click(next);
  });
}

/**
 * Fill the name on the Identity step (step 2 scratch), choose top-level,
 * and advance to Execution (step 3 scratch).
 */
async function fillIdentityAndAdvance(name = "acme") {
  const nameInput = screen.getByPlaceholderText(
    /engineering-team/i,
  ) as HTMLInputElement;
  if (nameInput.value === "") {
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: name } });
    });
  }
  // #814: step 2 scratch requires an explicit parent choice.
  await act(async () => {
    fireEvent.click(screen.getByTestId("parent-choice-top-level"));
  });
  const next = screen.getByRole("button", { name: /^next$/i });
  await act(async () => {
    fireEvent.click(next);
  });
}

/**
 * Advance through the wizard to the Execution step (step 3 scratch):
 * Source → scratch → Identity → Execution.
 */
async function advanceToExecution(name = "acme") {
  await selectScratchSource();
  await fillIdentityAndAdvance(name);
}

async function selectTool(value: string) {
  const toolSelect = screen.getByLabelText("Execution tool") as HTMLSelectElement;
  await act(async () => {
    fireEvent.change(toolSelect, { target: { value } });
  });
}

/** #814: click the "Top-level" radio button on the Identity step. */
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
  listConnectorTypes.mockResolvedValue([]);
  listPackages.mockResolvedValue([]);
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

// ---------------------------------------------------------------------------
// Source step — step 1 of the new wizard (ADR-0035 decision 5, #1563)
// ---------------------------------------------------------------------------
describe("CreateUnitPage — step 1: Source selection (#1563)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
  });

  it("renders three source cards on step 1", async () => {
    renderPage();
    expect(screen.getByTestId("source-card-catalog")).toBeInTheDocument();
    expect(screen.getByTestId("source-card-browse")).toBeInTheDocument();
    expect(screen.getByTestId("source-card-scratch")).toBeInTheDocument();
  });

  it("blocks Next when no source is selected", async () => {
    renderPage();
    const next = screen.getByRole("button", { name: /^next$/i });
    await act(async () => {
      fireEvent.click(next);
    });
    // Still on step 1 — source cards still visible.
    expect(screen.getByTestId("source-card-catalog")).toBeInTheDocument();
  });

  it("advances to the scratch Identity step when Scratch is selected", async () => {
    renderPage();
    await act(async () => {
      fireEvent.click(screen.getByTestId("source-card-scratch"));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /^next$/i }));
    });
    // Now on step 2 scratch (Identity): name input present.
    expect(
      screen.getByPlaceholderText(/engineering-team/i),
    ).toBeInTheDocument();
  });

  it("advances to the catalog Package step when Catalog is selected", async () => {
    renderPage();
    await act(async () => {
      fireEvent.click(screen.getByTestId("source-card-catalog"));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /^next$/i }));
    });
    // Now on step 2 catalog (Package): packages list visible.
    await waitFor(() => {
      expect(screen.getByText(/Select a package/i)).toBeInTheDocument();
    });
  });

  it("shows a browse Coming Soon stub when Browse is selected and Next is clicked", async () => {
    renderPage();
    await act(async () => {
      fireEvent.click(screen.getByTestId("source-card-browse"));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /^next$/i }));
    });
    expect(screen.getByTestId("browse-coming-soon")).toBeInTheDocument();
    // Browse is step 2 of 2 — the wizard shows no "Next" button on
    // the final step. The "Install" button is also absent (browse
    // has no submit path in v0.1); only "Back" is present.
    expect(screen.queryByRole("button", { name: /^next$/i })).not.toBeInTheDocument();
    expect(screen.queryByTestId("install-unit-button")).not.toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Catalog branch — Source → Package → Connector → Install
// ---------------------------------------------------------------------------
describe("CreateUnitPage — catalog branch (#1563)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
  });

  it("shows installed packages in the package picker", async () => {
    listPackages.mockResolvedValue([
      {
        name: "spring-voyage/software-engineering",
        description: "Software engineering team package.",
        unitTemplateCount: 3,
        agentTemplateCount: 2,
        skillCount: 1,
      },
    ]);

    renderPage();
    // Advance to step 2 catalog.
    await act(async () => {
      fireEvent.click(screen.getByTestId("source-card-catalog"));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /^next$/i }));
    });

    const pkgBtn = await screen.findByTestId(
      "package-option-spring-voyage/software-engineering",
    );
    expect(pkgBtn).toBeInTheDocument();
  });

  it("shows empty-catalog message when no packages are installed", async () => {
    listPackages.mockResolvedValue([]);

    renderPage();
    await act(async () => {
      fireEvent.click(screen.getByTestId("source-card-catalog"));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /^next$/i }));
    });

    await waitFor(() => {
      expect(
        screen.getByText(/no packages are installed/i),
      ).toBeInTheDocument();
    });
  });

  it("drives through catalog install and redirects on active status", async () => {
    listPackages.mockResolvedValue([
      {
        name: "spring-voyage/software-engineering",
        description: "Software engineering team package.",
        unitTemplateCount: 3,
        agentTemplateCount: 0,
        skillCount: 0,
      },
    ]);
    // Return "active" immediately from the initial POST so the polling
    // useEffect fires in the same test tick without needing fake timers.
    const activeStatus: InstallStatusResponse = {
      installId: "install-42",
      status: "active",
      packages: [],
      startedAt: new Date().toISOString(),
      completedAt: new Date().toISOString(),
      error: null,
    };
    installPackages.mockResolvedValue(activeStatus);
    getInstallStatus.mockResolvedValue(activeStatus);

    renderPage();

    const clickNext = async () => {
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: /^next$/i }));
      });
    };

    // Step 1: choose Catalog.
    await act(async () => {
      fireEvent.click(screen.getByTestId("source-card-catalog"));
    });
    await clickNext(); // → Package

    // Step 2: pick the package.
    const pkgBtn = await screen.findByTestId(
      "package-option-spring-voyage/software-engineering",
    );
    await act(async () => {
      fireEvent.click(pkgBtn);
    });
    await clickNext(); // → Connector

    await clickNext(); // → Install (step 4 catalog)

    // Click the Install button.
    const installBtn = await screen.findByTestId("install-unit-button");
    await act(async () => {
      fireEvent.click(installBtn);
    });

    await waitFor(() => {
      expect(installPackages).toHaveBeenCalledTimes(1);
    });
    expect(installPackages).toHaveBeenCalledWith([
      { packageName: "spring-voyage/software-engineering", inputs: {} },
    ]);

    // Poll returns active → redirect to /units.
    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith("/units");
    });
  });
});

// ---------------------------------------------------------------------------
// Scratch branch — Source → Identity → Execution → Connector → Install
// ---------------------------------------------------------------------------
describe("CreateUnitPage — scratch branch (#1563)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
  });

  it("drives through scratch install and redirects on active status", async () => {
    const pendingStatus: InstallStatusResponse = {
      installId: "install-scratch-1",
      status: "staging",
      packages: [],
      startedAt: new Date().toISOString(),
      completedAt: null,
      error: null,
    };
    const activeStatus: InstallStatusResponse = {
      ...pendingStatus,
      status: "active",
      completedAt: new Date().toISOString(),
    };
    installPackageFile.mockResolvedValue(pendingStatus);
    getInstallStatus.mockResolvedValue(activeStatus);

    renderPage();

    const clickNext = async () => {
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: /^next$/i }));
      });
    };

    // Step 1: Scratch.
    await act(async () => {
      fireEvent.click(screen.getByTestId("source-card-scratch"));
    });
    await clickNext(); // → Identity

    // Step 2: Identity.
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "acme" } });
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId("parent-choice-top-level"));
    });
    await clickNext(); // → Execution

    // Step 3: Execution — wait for model to seed.
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });
    await clickNext(); // → Connector

    await clickNext(); // → Install (step 5 scratch)

    // Click the Install button.
    const installBtn = await screen.findByTestId("install-unit-button");
    await act(async () => {
      fireEvent.click(installBtn);
    });

    await waitFor(() => {
      expect(installPackageFile).toHaveBeenCalledTimes(1);
    });

    // Poll returns active → redirect to /units.
    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith("/units");
    });
  });

  it("shows Retry and Abort buttons when install fails", async () => {
    const failedStatus: InstallStatusResponse = {
      installId: "install-fail-1",
      status: "failed",
      packages: [],
      startedAt: new Date().toISOString(),
      completedAt: new Date().toISOString(),
      error: "Package validation failed.",
    };
    installPackageFile.mockResolvedValue(failedStatus);
    getInstallStatus.mockResolvedValue(failedStatus);
    retryInstall.mockResolvedValue({
      installId: "install-fail-1-retry",
      status: "staging",
      packages: [],
      startedAt: new Date().toISOString(),
      completedAt: null,
      error: null,
    });

    renderPage();

    const clickNext = async () => {
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: /^next$/i }));
      });
    };

    await act(async () => {
      fireEvent.click(screen.getByTestId("source-card-scratch"));
    });
    await clickNext(); // → Identity

    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "fail-unit" } });
    });
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
    await clickNext(); // → Connector
    await clickNext(); // → Install

    const installBtn = await screen.findByTestId("install-unit-button");
    await act(async () => {
      fireEvent.click(installBtn);
    });

    // Wait for failed status to render retry/abort buttons.
    await waitFor(() => {
      expect(screen.getByTestId("install-retry-button")).toBeInTheDocument();
    });
    expect(screen.getByTestId("install-abort-button")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Execution step (step 3 scratch) — agent runtimes catalog (#690)
// ---------------------------------------------------------------------------
describe("CreateUnitPage — wizard reads tenant-installed agent runtimes (#690)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
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
  // from the live Ollama catalog.
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

// ---------------------------------------------------------------------------
// Credential-status banner (preserved — now on Execution step 3 scratch)
// ---------------------------------------------------------------------------
describe("CreateUnitPage — credential-status banner (#598, preserved post-T-07)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
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

// ---------------------------------------------------------------------------
// Step 3 scratch explains a disabled Next (#949 era — preserved)
// ---------------------------------------------------------------------------
describe("CreateUnitPage — Step 3 scratch explains a disabled Next", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
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

// ---------------------------------------------------------------------------
// Provider help links (#659)
// ---------------------------------------------------------------------------
describe("CreateUnitPage — provider help links (#659)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
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

// ---------------------------------------------------------------------------
// #1034 / #1563: renderNameSummary pure helper
// ---------------------------------------------------------------------------
describe("CreateUnitPage — renderNameSummary (#1034, updated for #1563)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
  });

  it("echoes the typed name when provided", async () => {
    const { renderNameSummary } = await import("./page");
    expect(
      renderNameSummary({
        name: "acme",
        source: "scratch",
        catalogPackageName: null,
      }),
    ).toBe("acme");
  });

  it("returns the package label when name is blank and source=catalog", async () => {
    const { renderNameSummary } = await import("./page");
    expect(
      renderNameSummary({
        name: "",
        source: "catalog",
        catalogPackageName: "spring-voyage/software-engineering",
      }),
    ).toBe("(from package spring-voyage/software-engineering)");
  });

  it("returns the package label when name is whitespace-only", async () => {
    const { renderNameSummary } = await import("./page");
    expect(
      renderNameSummary({
        name: "   ",
        source: "catalog",
        catalogPackageName: "spring-voyage/software-engineering",
      }),
    ).toBe("(from package spring-voyage/software-engineering)");
  });

  it("returns em-dash when name is blank and source=scratch", async () => {
    const { renderNameSummary } = await import("./page");
    expect(
      renderNameSummary({
        name: "",
        source: "scratch",
        catalogPackageName: null,
      }),
    ).toBe("—");
  });

  it("returns em-dash when source is null", async () => {
    const { renderNameSummary } = await import("./page");
    expect(
      renderNameSummary({
        name: "",
        source: null,
        catalogPackageName: null,
      }),
    ).toBe("—");
  });
});

// ---------------------------------------------------------------------------
// #1132: wizard state persistence across page reloads
// ---------------------------------------------------------------------------
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

  it("rehydrates the wizard at the saved step with the saved field values (schema v3)", async () => {
    // Schema v3: source/catalogPackageName/catalogInputs replace
    // mode/templateId/yamlText/yamlFileName.
    seedSnapshot({
      schemaVersion: 3,
      currentStep: 3,
      form: {
        source: "scratch",
        catalogPackageName: null,
        catalogInputs: {},
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
        connectorSlug: null,
        connectorTypeId: null,
        connectorConfig: null,
        parentUnitId: null,
        parentChoice: "top-level",
        parentUnitIds: [],
      },
    });

    renderPage();

    // Step 3 scratch (Execution) shows the tool select. Without
    // rehydrate the wizard would mount at step 1 (Source).
    await waitFor(() => {
      expect(screen.getByLabelText("Execution tool")).toBeInTheDocument();
    });

    // Stepping back once (Execution → Identity) restores the name field —
    // proves the form snapshot landed in component state.
    const back = screen.getByRole("button", { name: /^back$/i });
    await act(async () => {
      fireEvent.click(back);
    });
    // Now on Identity (step 2 scratch) — name input visible with saved value.
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
    seedSnapshot({
      schemaVersion: 999,
      currentStep: 4,
      form: {
        name: "should-not-rehydrate",
      },
    });

    renderPage();

    // Step 1 (Source) renders the source cards.
    await waitFor(() => {
      expect(screen.getByTestId("source-card-catalog")).toBeInTheDocument();
    });
  });

  it("discards a snapshot whose JSON is malformed", async () => {
    seedSnapshot("not-json{");

    renderPage();

    // Back at step 1.
    expect(screen.getByTestId("source-card-catalog")).toBeInTheDocument();
  });

  it("clears the snapshot when the operator clicks Cancel", async () => {
    renderPage();
    // Advance to scratch Identity so the name field is visible.
    await act(async () => {
      fireEvent.click(screen.getByTestId("source-card-scratch"));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /^next$/i }));
    });

    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "abandoned" } });
    });

    // Wait for the debounced save to settle.
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

// ---------------------------------------------------------------------------
// #1150: sub-unit creation via ?parent= URL param (scratch branch)
// ---------------------------------------------------------------------------
describe("CreateUnitPage — #1150 sub-unit creation", () => {
  const ORIGINAL_LOCATION = window.location;

  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
  });

  afterEachRestoreLocation();

  function setSearch(search: string) {
    Object.defineProperty(window, "location", {
      configurable: true,
      writable: true,
      value: { ...ORIGINAL_LOCATION, search },
    });
  }

  function afterEachRestoreLocation() {
    afterEach(() => {
      Object.defineProperty(window, "location", {
        configurable: true,
        writable: true,
        value: ORIGINAL_LOCATION,
      });
    });
  }

  it("seeds has-parents choice from URL param", async () => {
    setSearch("?parent=engineering-team");

    getUnit.mockResolvedValue({
      id: "engineering-team",
      name: "engineering-team",
      displayName: "Engineering Team",
      description: "Parent unit fixture.",
      registeredAt: "2026-04-21T00:00:00Z",
      status: "Running",
      model: null,
      color: null,
      tool: null,
      provider: null,
      hosting: null,
      lastValidationError: null,
      lastValidationRunId: null,
    });

    renderPage();

    // Advance past Source step to see Identity step where the parent-choice
    // radio is rendered.
    await act(async () => {
      fireEvent.click(screen.getByTestId("source-card-scratch"));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /^next$/i }));
    });

    // The "Has parent units" radio should be pre-selected.
    await waitFor(() => {
      expect(
        screen.getByTestId("parent-choice-has-parents").getAttribute("aria-checked"),
      ).toBe("true");
    });
    // The parent picker is visible.
    expect(await screen.findByTestId("parent-unit-picker")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// #814: parent-unit picker — explicit top-level vs has-parents choice
// ---------------------------------------------------------------------------
describe("CreateUnitPage — #814 parent-unit picker", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
  });

  it("blocks Next on Identity step when no parent choice is made", async () => {
    // When parentChoice is null, canGoNext is false — the Next button is
    // disabled in the DOM. HTML disabled buttons don't fire click events,
    // so we assert the button is disabled rather than expecting an error
    // message (which only renders after a failed Next attempt via keyboard).
    renderPage();
    await selectScratchSource();

    // Fill the name but do NOT pick top-level or has-parents.
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "blocked" } });
    });

    const next = screen.getByRole("button", { name: /^next$/i });
    expect(next).toBeDisabled();
    // Still on step 2 (Identity).
    expect(screen.getByPlaceholderText(/engineering-team/i)).toBeInTheDocument();
  });

  it("blocks Next when has-parents is chosen but no unit is selected", async () => {
    // parentChoice === "has-parents" but parentUnitIds is empty →
    // canGoNext is false → Next button is disabled.
    renderPage();
    await selectScratchSource();

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
    expect(next).toBeDisabled();
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
    await selectScratchSource();

    await act(async () => {
      fireEvent.click(screen.getByTestId("parent-choice-has-parents"));
    });

    const picker = await screen.findByTestId("parent-unit-picker");
    expect(picker).toBeInTheDocument();
    expect(
      await screen.findByTestId("parent-option-eng-unit-id"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("parent-option-product-unit-id"),
    ).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// #968 / #622: image-reference suggestions (scratch Execution step)
// ---------------------------------------------------------------------------
describe("CreateUnitPage — #968/#622 image-reference suggestions", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    sessionStorage.clear();
    mockLoadImageHistory.mockReturnValue([]);
  });

  it("shows no datalist when history is empty", async () => {
    mockLoadImageHistory.mockReturnValue([]);
    renderPage();
    await advanceToExecution();

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

    const datalist = document.getElementById("image-history-suggestions");
    expect(datalist).not.toBeNull();
    const options = datalist!.querySelectorAll("option");
    const values = Array.from(options).map((o) => o.value);
    expect(values).toContain("ghcr.io/spring-voyage/agent:latest");
    expect(values).toContain("localhost/spring-agent:dev");

    const imageInput = screen.getByLabelText(/^execution image$/i);
    expect(imageInput.getAttribute("list")).toBe("image-history-suggestions");
  });

  it("calls recordImageReference after successful scratch install", async () => {
    const pendingStatus: InstallStatusResponse = {
      installId: "install-img-test",
      status: "staging",
      packages: [],
      startedAt: new Date().toISOString(),
      completedAt: null,
      error: null,
    };
    const activeStatus: InstallStatusResponse = {
      ...pendingStatus,
      status: "active",
      completedAt: new Date().toISOString(),
    };
    installPackageFile.mockResolvedValue(pendingStatus);
    getInstallStatus.mockResolvedValue(activeStatus);

    renderPage();

    const clickNext = async () => {
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: /^next$/i }));
      });
    };

    await act(async () => {
      fireEvent.click(screen.getByTestId("source-card-scratch"));
    });
    await clickNext(); // → Identity

    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value: "img-test" } });
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId("parent-choice-top-level"));
    });
    await clickNext(); // → Execution

    // Fill in an image reference on the Execution step.
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
    await clickNext(); // → Connector
    await clickNext(); // → Install

    const installBtn = await screen.findByTestId("install-unit-button");
    await act(async () => {
      fireEvent.click(installBtn);
    });

    await waitFor(() => {
      expect(installPackageFile).toHaveBeenCalledTimes(1);
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
// #1509: categorizeWarning — pure unit tests (unchanged)
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
