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

import type {
  InstalledModelProviderResponse,
  InstallStatusResponse,
  UnitResponse,
} from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Mocks
//
// ADR-0038: the new-agent page reads four endpoints and calls three
// write endpoints:
//   - api.listUnits                     (initial-assignment picker)
//   - api.listModelProviders /
//     api.getModelProviderModels        (model dropdown)
//   - api.installPackageFile            (submit — replaces createAgent)
//   - api.getInstallStatus              (polling)
//   - api.assignUnitAgent               (post-install membership wiring)
// ---------------------------------------------------------------------------
const listUnits = vi.fn();
const listModelProviders = vi.fn();
const getModelProviderModels = vi.fn();
const installPackageFile = vi.fn();
const getInstallStatus = vi.fn();
const assignUnitAgent = vi.fn();
const retryInstall = vi.fn();
const abortInstall = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    listUnits: () => listUnits(),
    listModelProviders: () => listModelProviders(),
    getModelProviderModels: (id: string) => getModelProviderModels(id),
    installPackageFile: (yaml: string) => installPackageFile(yaml),
    getInstallStatus: (id: string) => getInstallStatus(id),
    assignUnitAgent: (unitId: string, agentId: string) =>
      assignUnitAgent(unitId, agentId),
    retryInstall: (id: string) => retryInstall(id),
    abortInstall: (id: string) => abortInstall(id),
  },
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

const pushMock = vi.fn();
const backMock = vi.fn();
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: pushMock, back: backMock }),
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

import CreateAgentPage from "./page";
import { buildAgentPackageYaml } from "./build-agent-package";

// ---------------------------------------------------------------------------
// Factory helpers
// ---------------------------------------------------------------------------

function makeUnit(overrides: Partial<UnitResponse> = {}): UnitResponse {
  return {
    id: overrides.id ?? "unit-id-alpha",
    name: overrides.name ?? "alpha",
    displayName: overrides.displayName ?? "Alpha",
    description: overrides.description ?? "",
    registeredAt: overrides.registeredAt ?? new Date().toISOString(),
    status: overrides.status ?? "Stopped",
    model: overrides.model ?? null,
    color: overrides.color ?? null,
    // ADR-0038 (PR-1b): the legacy flat `provider` field is gone from
    // `UnitResponse` — the model provider lives inside `model.provider`
    // on the execution block, not on the unit row.
    hosting: overrides.hosting ?? null,
  } as UnitResponse;
}

function makeRuntime(
  overrides: Partial<InstalledModelProviderResponse> = {},
): InstalledModelProviderResponse {
  const now = new Date().toISOString();
  // ADR-0038 (PR-1b): the install row no longer carries `kind` /
  // `defaultImage` — those fields belonged to the legacy
  // `InstalledAgentRuntimeResponse` (pre-ADR-0038) and moved into
  // `runtime-catalog.yaml`.
  return {
    id: overrides.id ?? "anthropic",
    displayName: overrides.displayName ?? "Anthropic",
    installedAt: overrides.installedAt ?? now,
    updatedAt: overrides.updatedAt ?? now,
    models: overrides.models ?? ["claude-3-5-sonnet"],
    defaultModel: overrides.defaultModel ?? "claude-3-5-sonnet",
    baseUrl: overrides.baseUrl ?? null,
    credentialKind: overrides.credentialKind ?? "ApiKey",
    credentialDisplayHint: overrides.credentialDisplayHint ?? null,
    credentialSecretName:
      overrides.credentialSecretName ?? "anthropic-api-key",
  } as InstalledModelProviderResponse;
}

function makeInstallStatus(
  overrides: Partial<InstallStatusResponse> = {},
): InstallStatusResponse {
  return {
    installId: overrides.installId ?? "install-id-1",
    status: overrides.status ?? "active",
    packages: overrides.packages ?? [
      { packageName: "ada", state: "active", errorMessage: null },
    ],
    startedAt: overrides.startedAt ?? new Date().toISOString(),
    completedAt: overrides.completedAt ?? new Date().toISOString(),
    error: overrides.error ?? null,
  };
}

// ---------------------------------------------------------------------------
// Render helper
// ---------------------------------------------------------------------------

function renderPage(): { client: QueryClient } {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
  }
  render(<CreateAgentPage />, { wrapper: Wrapper });
  return { client };
}

// ---------------------------------------------------------------------------
// Setup
// ---------------------------------------------------------------------------

beforeEach(() => {
  vi.clearAllMocks();
  listUnits.mockResolvedValue([makeUnit()]);
  listModelProviders.mockResolvedValue([makeRuntime()]);
  getModelProviderModels.mockResolvedValue([
    { id: "claude-3-5-sonnet", displayName: "Claude 3.5 Sonnet" },
  ]);
  // Default: install returns active immediately (no polling needed).
  installPackageFile.mockResolvedValue(
    makeInstallStatus({ status: "active", installId: "install-id-1" }),
  );
  getInstallStatus.mockResolvedValue(
    makeInstallStatus({ status: "active", installId: "install-id-1" }),
  );
  assignUnitAgent.mockResolvedValue(undefined);
  retryInstall.mockResolvedValue(
    makeInstallStatus({ status: "active", installId: "install-id-1" }),
  );
  abortInstall.mockResolvedValue(undefined);
});

afterEach(() => {
  // Restore real timers if any test enabled fake timers.
  vi.useRealTimers();
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe("CreateAgentPage", () => {
  // ── Render ────────────────────────────────────────────────────────────

  it("renders the form with id, displayName, role, execution and unit picker", async () => {
    renderPage();

    expect(
      screen.getByRole("heading", { name: /create a new agent/i }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText(/agent id/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/display name/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/^role$/i)).toBeInTheDocument();
    // ADR-0038: the wizard exposes Agent Runtime + Model fields. The
    // legacy "Container runtime" dropdown was retired — container
    // runtime is platform configuration, not an operator choice.
    expect(screen.getByLabelText(/agent runtime/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/container image/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/^model$/i)).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });
  });

  // ── Validation ────────────────────────────────────────────────────────

  it("blocks submit and surfaces a validation message when no unit is selected", async () => {
    renderPage();

    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });

    fireEvent.change(screen.getByLabelText(/agent id/i), {
      target: { value: "ada" },
    });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada" },
    });

    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    await waitFor(() => {
      expect(screen.getByTestId("agent-create-error")).toHaveTextContent(
        /pick at least one unit/i,
      );
    });
    expect(installPackageFile).not.toHaveBeenCalled();
  });

  it("rejects ids that violate the URL-safe pattern before posting", async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByLabelText(/assign to alpha/i));
    fireEvent.change(screen.getByLabelText(/agent id/i), {
      target: { value: "Ada Lovelace" },
    });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada" },
    });
    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    await waitFor(() => {
      expect(screen.getByTestId("agent-create-error")).toHaveTextContent(
        /url-safe/i,
      );
    });
    expect(installPackageFile).not.toHaveBeenCalled();
  });

  it("blocks submit when required displayName is missing", async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByLabelText(/assign to alpha/i));
    fireEvent.change(screen.getByLabelText(/agent id/i), {
      target: { value: "ada" },
    });
    // leave displayName empty

    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    await waitFor(() => {
      expect(screen.getByTestId("agent-create-error")).toHaveTextContent(
        /display name/i,
      );
    });
    expect(installPackageFile).not.toHaveBeenCalled();
  });

  // ── AgentPackage payload construction ─────────────────────────────────

  it("builds an AgentPackage YAML from form state", () => {
    const yaml = buildAgentPackageYaml({
      id: "ada",
      displayName: "Ada Lovelace",
      role: "reviewer",
      description: "Test agent",
      image: "ghcr.io/example/agent:latest",
      // ADR-0038: ai.runtime + ai.model.{provider,id} replace the
      // legacy flat ai.tool / ai.agent shape.
      runtime: "claude-code",
      modelProvider: "anthropic",
      modelId: "claude-3-5-sonnet",
      unitIds: ["alpha"],
    });

    // No `kind:` field; the agent body lives under content[].agent.
    expect(yaml).not.toContain("kind:");
    expect(yaml).toContain("content:");
    expect(yaml).toContain("- agent:");
    expect(yaml).toContain("name: ada");
    expect(yaml).toContain("id: ada");
    expect(yaml).toContain("Ada Lovelace");
    expect(yaml).toContain("role: reviewer");
    expect(yaml).toContain("description: Test agent");
    expect(yaml).toContain("image: ghcr.io/example/agent:latest");
    // ADR-0038 ai block.
    expect(yaml).toContain("runtime: claude-code");
    expect(yaml).toContain("provider: anthropic");
    expect(yaml).toContain("id: claude-3-5-sonnet");
  });

  it("omits optional fields from the YAML when they are empty", () => {
    const yaml = buildAgentPackageYaml({
      id: "ada",
      displayName: "Ada",
      unitIds: [],
    });

    expect(yaml).not.toContain("kind:");
    expect(yaml).toContain("content:");
    expect(yaml).toContain("- agent:");
    expect(yaml).toContain("name: ada");
    expect(yaml).not.toContain("role:");
    expect(yaml).not.toContain("description:");
    expect(yaml).not.toContain("execution:");
    expect(yaml).not.toContain("ai:");
  });

  // ── Submit → install endpoint ──────────────────────────────────────────

  it("submits via installPackageFile and redirects on active status", async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByLabelText(/assign to alpha/i));
    fireEvent.change(screen.getByLabelText(/agent id/i), {
      target: { value: "ada" },
    });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada Lovelace" },
    });
    fireEvent.change(screen.getByLabelText(/^role$/i), {
      target: { value: "reviewer" },
    });
    fireEvent.change(screen.getByLabelText(/container image/i), {
      target: { value: "ghcr.io/example/agent:latest" },
    });

    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    await waitFor(() => {
      expect(installPackageFile).toHaveBeenCalledTimes(1);
    });

    const yaml = installPackageFile.mock.calls[0][0] as string;
    expect(yaml).toContain("content:");
    expect(yaml).toContain("- agent:");
    expect(yaml).toContain("name: ada");
    expect(yaml).toContain("Ada Lovelace");
    expect(yaml).toContain("role: reviewer");
    expect(yaml).toContain("image: ghcr.io/example/agent:latest");

    await waitFor(() => {
      expect(assignUnitAgent).toHaveBeenCalledWith("alpha", "ada");
    });

    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith("/units?node=alpha&tab=Agents");
    });
  });

  it("calls the install endpoint with an agent content entry in the YAML body", async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByLabelText(/assign to alpha/i));
    fireEvent.change(screen.getByLabelText(/agent id/i), { target: { value: "bob" } });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Bob Builder" },
    });

    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    await waitFor(() => {
      expect(installPackageFile).toHaveBeenCalledTimes(1);
    });

    const yaml = installPackageFile.mock.calls[0][0] as string;
    expect(yaml).toMatch(/content:\s*\n\s*- agent:/);
    expect(yaml).not.toMatch(/^kind:/m);
  });

  // ── Multi-unit assignment ──────────────────────────────────────────────

  it("assigns agent to multiple units sequentially after install", async () => {
    listUnits.mockResolvedValue([
      makeUnit({ name: "alpha", displayName: "Alpha" }),
      makeUnit({ id: "unit-id-beta", name: "beta", displayName: "Beta" }),
    ]);

    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
      expect(screen.getByLabelText(/assign to beta/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByLabelText(/assign to alpha/i));
    fireEvent.click(screen.getByLabelText(/assign to beta/i));
    fireEvent.change(screen.getByLabelText(/agent id/i), { target: { value: "ada" } });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada" },
    });

    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    await waitFor(() => {
      expect(assignUnitAgent).toHaveBeenCalledWith("alpha", "ada");
      expect(assignUnitAgent).toHaveBeenCalledWith("beta", "ada");
    });

    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith(
        "/units?node=alpha&tab=Agents",
      );
    });
  });

  // ── No unit assignment ─────────────────────────────────────────────────

  it("installs without membership calls when no units are selected … but blocks on validation", async () => {
    // The form requires at least one unit, so "no units" is blocked at validation.
    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });

    fireEvent.change(screen.getByLabelText(/agent id/i), { target: { value: "ada" } });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada" },
    });
    // Don't check any unit.

    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    await waitFor(() => {
      expect(screen.getByTestId("agent-create-error")).toHaveTextContent(
        /pick at least one unit/i,
      );
    });
    expect(installPackageFile).not.toHaveBeenCalled();
    expect(assignUnitAgent).not.toHaveBeenCalled();
  });

  // ── Install failure → retry/abort UI ──────────────────────────────────

  it("renders retry and abort buttons when install returns failed status", async () => {
    installPackageFile.mockResolvedValue(
      makeInstallStatus({
        status: "failed",
        installId: "install-id-fail",
        packages: [
          {
            packageName: "ada",
            state: "failed",
            errorMessage: "Dapr placement timeout",
          },
        ],
      }),
    );

    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByLabelText(/assign to alpha/i));
    fireEvent.change(screen.getByLabelText(/agent id/i), { target: { value: "ada" } });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada" },
    });

    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    await waitFor(() => {
      expect(screen.getByTestId("install-failed-panel")).toBeInTheDocument();
    });

    expect(screen.getByTestId("retry-button")).toBeInTheDocument();
    expect(screen.getByTestId("abort-button")).toBeInTheDocument();
    expect(screen.getByTestId("install-failed-panel")).toHaveTextContent(
      /dapr placement timeout/i,
    );
  });

  it("renders retry and abort buttons when polling returns failed", async () => {
    installPackageFile.mockResolvedValue(
      makeInstallStatus({
        status: "staging",
        installId: "install-id-poll-fail",
        packages: [{ packageName: "ada", state: "staging", errorMessage: null }],
      }),
    );
    getInstallStatus.mockResolvedValue(
      makeInstallStatus({
        status: "failed",
        installId: "install-id-poll-fail",
        packages: [
          { packageName: "ada", state: "failed", errorMessage: "Container pull failed" },
        ],
      }),
    );

    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByLabelText(/assign to alpha/i));
    fireEvent.change(screen.getByLabelText(/agent id/i), { target: { value: "ada" } });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada" },
    });

    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    // The polling loop waits POLL_INTERVAL_MS (2 s) before the first poll.
    // waitFor will retry for up to 8 s, which is sufficient.
    await waitFor(
      () => {
        expect(screen.getByTestId("install-failed-panel")).toBeInTheDocument();
      },
      { timeout: 8_000 },
    );

    expect(screen.getByTestId("retry-button")).toBeInTheDocument();
    expect(screen.getByTestId("abort-button")).toBeInTheDocument();
  }, 10_000);

  // ── Membership-add partial failure ────────────────────────────────────

  it("surfaces a partial-success message when membership add fails for one unit", async () => {
    listUnits.mockResolvedValue([
      makeUnit({ name: "alpha", displayName: "Alpha" }),
      makeUnit({ id: "unit-id-beta", name: "beta", displayName: "Beta" }),
    ]);
    assignUnitAgent
      .mockResolvedValueOnce(undefined)             // alpha succeeds
      .mockRejectedValueOnce(new Error("Forbidden")); // beta fails

    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
      expect(screen.getByLabelText(/assign to beta/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByLabelText(/assign to alpha/i));
    fireEvent.click(screen.getByLabelText(/assign to beta/i));
    fireEvent.change(screen.getByLabelText(/agent id/i), { target: { value: "ada" } });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada" },
    });

    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    // Should surface partial error message.
    await waitFor(() => {
      expect(screen.getByTestId("agent-create-error")).toHaveTextContent(
        /membership in beta could not be added/i,
      );
    });

    // The failed unit's row should show the error inline.
    await waitFor(() => {
      expect(screen.getByText(/failed: forbidden/i)).toBeInTheDocument();
    });

    // The successful unit's row should show success.
    await waitFor(() => {
      expect(screen.getByText(/membership added/i)).toBeInTheDocument();
    });
  });

  // ── API error message ──────────────────────────────────────────────────

  it("surfaces an API error message inline (4xx from install endpoint)", async () => {
    installPackageFile.mockRejectedValueOnce(
      new Error("Package name 'ada' already exists in this tenant."),
    );

    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByLabelText(/assign to alpha/i));
    fireEvent.change(screen.getByLabelText(/agent id/i), {
      target: { value: "ada" },
    });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada" },
    });
    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    await waitFor(() => {
      expect(screen.getByTestId("agent-create-error")).toHaveTextContent(
        /already exists/i,
      );
    });
    expect(pushMock).not.toHaveBeenCalled();
  });

  // ── Retry button triggers retryInstall ────────────────────────────────

  it("retry button calls retryInstall and redirects on active", async () => {
    installPackageFile.mockResolvedValue(
      makeInstallStatus({
        status: "failed",
        installId: "install-id-fail",
        packages: [
          { packageName: "ada", state: "failed", errorMessage: "Phase 2 error" },
        ],
      }),
    );

    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByLabelText(/assign to alpha/i));
    fireEvent.change(screen.getByLabelText(/agent id/i), { target: { value: "ada" } });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada" },
    });

    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    await waitFor(() => {
      expect(screen.getByTestId("retry-button")).toBeInTheDocument();
    });

    await act(async () => {
      fireEvent.click(screen.getByTestId("retry-button"));
    });

    await waitFor(() => {
      expect(retryInstall).toHaveBeenCalledWith("install-id-fail");
    });

    await waitFor(() => {
      expect(assignUnitAgent).toHaveBeenCalledWith("alpha", "ada");
    });

    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith("/units?node=alpha&tab=Agents");
    });
  });

  // ── Abort button ──────────────────────────────────────────────────────

  it("abort button calls abortInstall and resets the form state", async () => {
    installPackageFile.mockResolvedValue(
      makeInstallStatus({
        status: "failed",
        installId: "install-id-abort",
        packages: [
          { packageName: "ada", state: "failed", errorMessage: "error" },
        ],
      }),
    );

    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByLabelText(/assign to alpha/i));
    fireEvent.change(screen.getByLabelText(/agent id/i), { target: { value: "ada" } });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada" },
    });

    fireEvent.click(screen.getByRole("button", { name: /create agent/i }));

    await waitFor(() => {
      expect(screen.getByTestId("abort-button")).toBeInTheDocument();
    });

    await act(async () => {
      fireEvent.click(screen.getByTestId("abort-button"));
    });

    await waitFor(() => {
      expect(abortInstall).toHaveBeenCalledWith("install-id-abort");
    });

    // The install-failed panel should be gone after abort.
    await waitFor(() => {
      expect(screen.queryByTestId("install-failed-panel")).not.toBeInTheDocument();
    });
  });
});

// ---------------------------------------------------------------------------
// Runtime-aware provider banner + picker (Bug 1 / Bug 2)
// ---------------------------------------------------------------------------

describe("CreateAgentPage — runtime-aware provider banner + picker", () => {
  it("names the missing fixed provider on Claude Code when no providers are installed", async () => {
    listModelProviders.mockResolvedValue([]);

    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/agent runtime/i)).toBeInTheDocument();
    });

    const banner = await screen.findByTestId("model-provider-catalog-issue");
    expect(banner.textContent).toMatch(
      /Claude Code requires the anthropic model provider/i,
    );
  });

  it("does not show a banner on Claude Code when Anthropic is installed", async () => {
    // The default beforeEach already mocks an anthropic install.
    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/agent runtime/i)).toBeInTheDocument();
    });
    expect(
      screen.queryByTestId("model-provider-catalog-issue"),
    ).not.toBeInTheDocument();
  });

  it("warns runtime-specifically on Spring Voyage Agent when no allowed provider is installed", async () => {
    listModelProviders.mockResolvedValue([]);

    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/agent runtime/i)).toBeInTheDocument();
    });

    const runtimeSelect = screen.getByLabelText(
      /agent runtime/i,
    ) as HTMLSelectElement;
    await act(async () => {
      fireEvent.change(runtimeSelect, { target: { value: "spring-voyage" } });
    });

    const banner = await screen.findByTestId("model-provider-catalog-issue");
    expect(banner.textContent).toMatch(
      /Spring Voyage Agent needs at least one model provider installed/i,
    );

    // Picker is rendered but disabled and shows a placeholder option.
    const providerSelect = screen.getByLabelText(
      /^model provider$/i,
    ) as HTMLSelectElement;
    expect(providerSelect).toBeDisabled();
    const optionTexts = Array.from(providerSelect.options).map(
      (o) => o.textContent ?? "",
    );
    expect(optionTexts.some((t) => /no providers installed/i.test(t))).toBe(
      true,
    );
  });

  it("preselects the first allowed provider on Spring Voyage Agent (Bug 2)", async () => {
    listModelProviders.mockResolvedValue([
      makeRuntime({
        id: "anthropic",
        displayName: "Anthropic",
        models: ["claude-3-5-sonnet"],
        defaultModel: "claude-3-5-sonnet",
      }),
      makeRuntime({
        id: "openai",
        displayName: "OpenAI",
        models: ["gpt-4o"],
        defaultModel: "gpt-4o",
      }),
    ]);

    renderPage();
    await waitFor(() => {
      expect(screen.getByLabelText(/agent runtime/i)).toBeInTheDocument();
    });

    const runtimeSelect = screen.getByLabelText(
      /agent runtime/i,
    ) as HTMLSelectElement;
    await act(async () => {
      fireEvent.change(runtimeSelect, { target: { value: "spring-voyage" } });
    });

    const providerSelect = (await screen.findByLabelText(
      /^model provider$/i,
    )) as HTMLSelectElement;
    expect(providerSelect).not.toBeDisabled();
    // First provider in the install list is anthropic; the picker
    // should auto-snap to it without operator interaction.
    await waitFor(() => {
      expect(providerSelect.value).toBe("anthropic");
    });
  });
});
