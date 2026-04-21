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

// Mock the API client. Post-#690 the wizard reads the provider+model
// catalog from `/api/v1/agent-runtimes` and validates credentials via
// `/api/v1/agent-runtimes/{id}/validate-credential`.
const listOllamaModels = vi.fn();
const listAgentRuntimes = vi.fn();
const getAgentRuntimeModels = vi.fn();
const validateAgentRuntimeCredential = vi.fn();
const getProviderCredentialStatus = vi.fn();
const createUnit = vi.fn();
const createUnitFromTemplate = vi.fn();
const createUnitFromYaml = vi.fn();
const createUnitSecret = vi.fn();
const createTenantSecret = vi.fn();
const rotateTenantSecret = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    listOllamaModels: () => listOllamaModels(),
    listAgentRuntimes: () => listAgentRuntimes(),
    getAgentRuntimeModels: (id: string) => getAgentRuntimeModels(id),
    validateAgentRuntimeCredential: (
      id: string,
      k: string,
      secretName?: string,
    ) => validateAgentRuntimeCredential(id, k, secretName),
    getProviderCredentialStatus: (p: string) => getProviderCredentialStatus(p),
    getUnitTemplates: vi.fn().mockResolvedValue([]),
    getConnectorTypes: vi.fn().mockResolvedValue([]),
    createUnit: (body: unknown) => createUnit(body),
    createUnitFromTemplate: (body: unknown) => createUnitFromTemplate(body),
    createUnitFromYaml: (body: unknown) => createUnitFromYaml(body),
    createUnitSecret: (unit: string, body: unknown) =>
      createUnitSecret(unit, body),
    createTenantSecret: (body: unknown) => createTenantSecret(body),
    rotateTenantSecret: (name: string, body: unknown) =>
      rotateTenantSecret(name, body),
  },
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
    models: ["claude-sonnet-4-20250514", "claude-opus-4-20250514"],
    defaultModel: "claude-sonnet-4-20250514",
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
        "claude-sonnet-4-20250514",
        "claude-opus-4-20250514",
        "claude-haiku-4-20250514",
      ],
      defaultModel: "claude-sonnet-4-20250514",
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
  validateAgentRuntimeCredential.mockResolvedValue({
    valid: true,
    status: "Valid",
    errorMessage: null,
  });
  getProviderCredentialStatus.mockResolvedValue(
    makeStatus({ provider: "anthropic", resolvable: true, source: "tenant" }),
  );
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
    expect(options).toContain("claude-sonnet-4-20250514");
    expect(options).toContain("claude-opus-4-20250514");
    expect(options).toContain("claude-haiku-4-20250514");
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

  it("routes validation through /api/v1/agent-runtimes/{id}/validate-credential", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "anthropic", resolvable: false, source: null }),
    );
    renderPage();
    await advanceToExecution();

    const input = (await screen.findByTestId(
      "credential-input",
    )) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(input, { target: { value: "sk-ant-test" } });
    });
    await act(async () => {
      fireEvent.blur(input);
    });

    await waitFor(() => {
      expect(validateAgentRuntimeCredential).toHaveBeenCalledWith(
        "claude",
        "sk-ant-test",
        undefined,
      );
    });
  });
});

describe("CreateUnitPage — credential-status banner (#598, preserved post-#690)", () => {
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

describe("CreateUnitPage — inline credential flow (#626)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
    createUnit.mockResolvedValue({ name: "acme", id: "acme-id" });
    createUnitSecret.mockResolvedValue({
      name: "anthropic-api-key",
      version: "v1",
    });
    createTenantSecret.mockResolvedValue({
      name: "anthropic-api-key",
      version: "v1",
    });
    rotateTenantSecret.mockResolvedValue({
      name: "anthropic-api-key",
      version: "v2",
    });
  });

  async function fillName(value: string) {
    const nameInput = screen.getByPlaceholderText(
      /engineering-team/i,
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(nameInput, { target: { value } });
    });
  }

  async function clickNext() {
    const next = screen.getByRole("button", { name: /^(next|validating…)$/i });
    await act(async () => {
      fireEvent.click(next);
    });
  }

  it("writes a unit-scoped secret when the tenant-default toggle is off", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "anthropic", resolvable: false, source: null }),
    );
    renderPage();
    await fillName("acme");
    await advanceToExecution();

    const input = (await screen.findByTestId(
      "credential-input",
    )) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(input, { target: { value: "sk-ant-unit" } });
    });
    await act(async () => {
      fireEvent.blur(input);
    });
    await waitFor(() => {
      expect(validateAgentRuntimeCredential).toHaveBeenCalled();
    });
    // After a successful validate the Model dropdown renders from the
    // agent-runtimes catalog; wait for the effect to snap a default.
    await waitFor(async () => {
      const modelSelect = (await screen.findByLabelText(
        /^Model$/i,
      )) as HTMLSelectElement;
      expect(modelSelect.value).not.toBe("");
    });

    // Drive Execution → Mode → Connector → Secrets → Finalize.
    await clickNext();
    const scratch = screen.getByRole("button", { name: /scratch/i });
    await act(async () => {
      fireEvent.click(scratch);
    });
    await clickNext();
    await clickNext();
    await clickNext();

    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(createUnit).toHaveBeenCalled();
    });
    expect(createTenantSecret).not.toHaveBeenCalled();
    expect(createUnitSecret).toHaveBeenCalledWith("acme", {
      name: "anthropic-api-key",
      value: "sk-ant-unit",
    });
  });

  it("writes a tenant-scoped secret when the toggle is on", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "anthropic", resolvable: false, source: null }),
    );
    renderPage();
    await fillName("acme");
    await advanceToExecution();

    const input = (await screen.findByTestId(
      "credential-input",
    )) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(input, { target: { value: "sk-ant-tenant" } });
    });
    await act(async () => {
      fireEvent.blur(input);
    });
    await waitFor(() => {
      expect(validateAgentRuntimeCredential).toHaveBeenCalled();
    });
    const toggle = screen.getByTestId(
      "credential-save-as-tenant-default",
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.click(toggle);
    });

    await clickNext();
    const scratch = screen.getByRole("button", { name: /scratch/i });
    await act(async () => {
      fireEvent.click(scratch);
    });
    await clickNext();
    await clickNext();
    await clickNext();

    const createBtn = screen.getByTestId("create-unit-button");
    await act(async () => {
      fireEvent.click(createBtn);
    });

    await waitFor(() => {
      expect(createTenantSecret).toHaveBeenCalledWith({
        name: "anthropic-api-key",
        value: "sk-ant-tenant",
      });
    });
    expect(createUnitSecret).not.toHaveBeenCalled();
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
// surface a human-readable reason. Previously an empty agent-runtime
// catalog (e.g. platform API down, or no installed runtime matching
// the selected tool) silently hid the credential + model surface and
// the operator was stuck staring at a disabled Next button with no
// clue what to do.
describe("CreateUnitPage — Step 2 explains a disabled Next", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    seedDefaultMocks();
  });

  it("warns when no agent runtimes are installed and Claude Code is selected", async () => {
    listAgentRuntimes.mockResolvedValue([]);
    renderPage();
    await advanceToExecution();

    const banner = await screen.findByTestId("agent-runtime-catalog-issue");
    expect(banner.textContent).toMatch(/no agent runtimes are installed/i);

    const next = screen.getByRole("button", { name: /^next$/i });
    expect(next).toBeDisabled();

    const reason = await screen.findByTestId("next-disabled-reason");
    expect(reason.textContent).toMatch(/no agent runtimes are installed/i);
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

  it("prompts for an API key when the credential probe says nothing is resolvable", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "anthropic", resolvable: false, source: null }),
    );
    renderPage();
    await advanceToExecution();

    const reason = await screen.findByTestId("next-disabled-reason");
    expect(reason.textContent).toMatch(/Anthropic API key/i);
    expect(screen.getByRole("button", { name: /^next$/i })).toBeDisabled();
  });
});
