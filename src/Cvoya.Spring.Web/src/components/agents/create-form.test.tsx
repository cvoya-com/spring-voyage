import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  InstalledModelProviderResponse,
  UnitResponse,
} from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Mocks — same surface as the page-level test so we don't hit the network
// during a "renders without crashing" smoke check.
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

import { AgentCreateForm } from "./create-form";

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
    hosting: overrides.hosting ?? null,
  } as UnitResponse;
}

function makeProvider(
  overrides: Partial<InstalledModelProviderResponse> = {},
): InstalledModelProviderResponse {
  const now = new Date().toISOString();
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

function renderForm(props: Parameters<typeof AgentCreateForm>[0] = {}) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
  }
  return render(<AgentCreateForm {...props} />, { wrapper: Wrapper });
}

// ---------------------------------------------------------------------------
// Setup
// ---------------------------------------------------------------------------

beforeEach(() => {
  vi.clearAllMocks();
  listUnits.mockResolvedValue([makeUnit()]);
  listModelProviders.mockResolvedValue([makeProvider()]);
  getModelProviderModels.mockResolvedValue([
    { id: "claude-3-5-sonnet", displayName: "Claude 3.5 Sonnet" },
  ]);
});

// ---------------------------------------------------------------------------
// Tests — minimal "renders without crashing" smoke check (ADR-0039 I3 acceptance).
// The full behavioural coverage lives in `app/agents/create/page.test.tsx`,
// which exercises the same component through the page wrapper.
// ---------------------------------------------------------------------------

describe("AgentCreateForm — smoke", () => {
  it("renders identity, execution, and unit-assignment sections without crashing", async () => {
    renderForm();

    expect(screen.getByLabelText(/agent id/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/display name/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/^role$/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/agent runtime/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/container image/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/^model$/i)).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });

    // The submit button always renders.
    expect(
      screen.getByRole("button", { name: /create agent/i }),
    ).toBeInTheDocument();
  });

  it("pre-selects units passed via the `initialUnitIds` prop (J1 entry-point)", async () => {
    renderForm({ initialUnitIds: ["alpha"] });

    const checkbox = await screen.findByLabelText(/assign to alpha/i);
    expect(checkbox).toBeChecked();
  });

  it("renders without `onSuccess` / `onCancel` callbacks", () => {
    // The form must tolerate a caller that wires neither callback —
    // the dialog (J1) might handle close-on-success out-of-band.
    expect(() => renderForm()).not.toThrow();
  });
});
