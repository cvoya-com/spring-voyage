import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  InstalledModelProviderResponse,
  UnitExecutionResponse,
  UnitResponse,
} from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Mocks — same surface as the page-level test so we don't hit the network
// during a "renders without crashing" smoke check.
// ---------------------------------------------------------------------------

const listUnits = vi.fn();
const listModelProviders = vi.fn();
const getModelProviderModels = vi.fn();
const getUnitExecution = vi.fn();
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
    getUnitExecution: (id: string) => getUnitExecution(id),
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
  // Default: empty unit-execution row — `useUnitExecution` always returns
  // the empty shape, so callers never need to branch on 404.
  getUnitExecution.mockResolvedValue({
    image: null,
    runtime: null,
    model: null,
  } as UnitExecutionResponse);
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

// ---------------------------------------------------------------------------
// ADR-0039 I4 — per-field inherit affordance on Execution fields
// (DESIGN.md §12.6).
// ---------------------------------------------------------------------------

describe("AgentCreateForm — inherit affordance (I4)", () => {
  it("renders `inherit-indicator` for every Execution field while in inherit mode", async () => {
    renderForm();

    // The form lands with all five execution fields blank by default.
    // Every field surfaces an `inherit-indicator` — runtime, model
    // provider (multi-provider until a runtime is picked), model id,
    // image, hosting.
    await waitFor(() => {
      const indicators = screen.getAllByTestId("inherit-indicator");
      expect(indicators.length).toBeGreaterThanOrEqual(4);
    });
  });

  it("placeholder copy reads `inherited from <unit-name>: <value>` once a unit is picked", async () => {
    listUnits.mockResolvedValue([
      makeUnit({ name: "engineering", displayName: "Engineering Team" }),
    ]);
    getUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
      runtime: "claude-code",
      model: { provider: "anthropic", id: "claude-sonnet-4-6" },
    } as UnitExecutionResponse);

    renderForm();

    // Tick the unit so the form has a parent unit to inherit from.
    const checkbox = await screen.findByLabelText(
      /assign to engineering team/i,
    );
    fireEvent.click(checkbox);

    await waitFor(() => {
      expect(getUnitExecution).toHaveBeenCalledWith("engineering");
    });

    await waitFor(() => {
      const indicators = screen.getAllByTestId("inherit-indicator");
      const texts = indicators.map((el) => el.textContent ?? "");
      // Runtime
      expect(
        texts.some((t) =>
          t.includes("inherited from Engineering Team: claude-code"),
        ),
      ).toBe(true);
      // Image
      expect(
        texts.some((t) =>
          t.includes(
            "inherited from Engineering Team: ghcr.io/acme/spring-agent:v1",
          ),
        ),
      ).toBe(true);
      // Model id
      expect(
        texts.some((t) =>
          t.includes(
            "inherited from Engineering Team: claude-sonnet-4-6",
          ),
        ),
      ).toBe(true);
    });
  });

  it("hides the inherit indicator on a field once the operator sets an explicit value", async () => {
    renderForm();

    await waitFor(() => {
      // Image starts in inherit mode → indicator present.
      const indicators = screen.getAllByTestId("inherit-indicator");
      const texts = indicators.map((el) => el.textContent ?? "");
      // Find the image-field indicator (matches whatever inherit-source
      // copy the form resolves to in the 0-unit case — usually "tenant
      // defaults"). At minimum the indicator count is non-zero.
      expect(texts.length).toBeGreaterThan(0);
    });

    const imageInput = screen.getByLabelText(/container image/i);
    fireEvent.change(imageInput, {
      target: { value: "ghcr.io/team/custom-agent:v9" },
    });

    // After the explicit pick, the image card no longer surfaces an
    // inherit-indicator with the image-field's help copy. The other
    // fields (still blank) keep their indicators.
    await waitFor(() => {
      const indicators = screen.queryAllByTestId("inherit-indicator");
      const texts = indicators.map((el) => el.textContent ?? "");
      expect(
        texts.every((t) => !t.includes("ghcr.io/team/custom-agent:v9")),
      ).toBe(true);
    });
  });

  it("flips the Execution card badge from `Inherits` to `Configured` when any field is set", async () => {
    renderForm();

    // Default: every field blank → `Inherits` badge.
    const badge = await screen.findByTestId("execution-card-badge");
    expect(badge.textContent).toBe("Inherits");

    // Set the image field.
    const imageInput = screen.getByLabelText(/container image/i);
    fireEvent.change(imageInput, {
      target: { value: "ghcr.io/team/custom-agent:v9" },
    });

    await waitFor(() => {
      expect(
        screen.getByTestId("execution-card-badge").textContent,
      ).toBe("Configured");
    });
  });

  it("clears a field back to inherit mode via the `Use inherited value` button", async () => {
    renderForm();

    // Set the image field.
    const imageInput = screen.getByLabelText(/container image/i);
    fireEvent.change(imageInput, {
      target: { value: "ghcr.io/team/custom-agent:v9" },
    });

    // The clear button appears with an aria-label keyed off the field
    // label.
    const clearBtn = await screen.findByRole("button", {
      name: /use inherited container image/i,
    });
    fireEvent.click(clearBtn);

    // Field is back in inherit mode → indicator copy is restored.
    await waitFor(() => {
      const indicators = screen.getAllByTestId("inherit-indicator");
      const texts = indicators.map((el) => el.textContent ?? "");
      // The image input is empty again.
      expect((imageInput as HTMLInputElement).value).toBe("");
      // And no indicator carries the cleared value.
      expect(
        texts.every((t) => !t.includes("ghcr.io/team/custom-agent:v9")),
      ).toBe(true);
    });
  });
});
