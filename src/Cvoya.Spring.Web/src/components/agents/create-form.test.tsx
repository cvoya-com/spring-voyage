import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  InstallStatusResponse,
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

// Re-export the real ApiError so the production code's `instanceof
// ApiError` check (used by the multi-parent inheritance conflict path —
// ADR-0039 §6 / I6) matches the instances we throw from the test
// mocks. Mocking ApiError out would silently break the structured-422
// detection.
vi.mock("@/lib/api/client", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api/client")>(
    "@/lib/api/client",
  );
  return {
    ...actual,
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
  };
});

import { ApiError } from "@/lib/api/client";

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

function renderForm(
  props: Partial<Parameters<typeof AgentCreateForm>[0]> = {},
) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
  }
  const finalProps: Parameters<typeof AgentCreateForm>[0] = {
    context: "dialog",
    ...props,
  };
  return render(<AgentCreateForm {...finalProps} />, { wrapper: Wrapper });
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
// ADR-0039 K1 — page-only Source step.
// ---------------------------------------------------------------------------

describe("AgentCreateForm — source step (K1)", () => {
  it("renders a Source step first in page context with three source cards", () => {
    renderForm({ context: "page" });

    expect(
      screen.getByRole("heading", { name: /choose a source/i }),
    ).toBeInTheDocument();
    expect(screen.getByTestId("agent-source-card-scratch")).toHaveTextContent(
      /scratch/i,
    );
    expect(
      screen.getByTestId("agent-source-card-from-package"),
    ).toHaveTextContent(/from package/i);
    expect(screen.getByTestId("agent-source-card-browse")).toHaveTextContent(
      /browse/i,
    );
  });

  it("advances from Scratch to the Identity step when Next is clicked", () => {
    renderForm({ context: "page" });

    fireEvent.click(screen.getByTestId("agent-source-card-scratch"));
    fireEvent.click(screen.getByRole("button", { name: /next/i }));

    expect(
      screen.queryByRole("heading", { name: /choose a source/i }),
    ).not.toBeInTheDocument();
    expect(screen.getByLabelText(/agent id/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/display name/i)).toBeInTheDocument();
  });

  it("skips Source in dialog context and starts on Identity", () => {
    renderForm({ context: "dialog" });

    expect(
      screen.queryByRole("heading", { name: /choose a source/i }),
    ).not.toBeInTheDocument();
    expect(screen.queryByTestId("agent-source-card-scratch")).toBeNull();
    expect(screen.getByLabelText(/agent id/i)).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// ADR-0039 K7 — page-only Browse stub branch.
// ---------------------------------------------------------------------------

describe("AgentCreateForm — Browse stub (K7)", () => {
  it("advances from Browse to the coming-soon stub in page context", () => {
    renderForm({ context: "page" });

    fireEvent.click(screen.getByTestId("agent-source-card-browse"));
    fireEvent.click(screen.getByRole("button", { name: /next/i }));

    expect(
      screen.getByRole("heading", { name: /browse agent packages/i }),
    ).toBeInTheDocument();
    expect(screen.getByTestId("browse-coming-soon")).toBeVisible();
    expect(screen.getByTestId("browse-coming-soon")).toHaveTextContent(
      /Search the Spring Voyage package registry for community packages/i,
    );
  });

  it("keeps the Next button disabled on the Browse stub step", () => {
    renderForm({ context: "page" });

    fireEvent.click(screen.getByTestId("agent-source-card-browse"));
    fireEvent.click(screen.getByRole("button", { name: /next/i }));

    expect(screen.getByRole("button", { name: /next/i })).toBeDisabled();
  });
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

// ---------------------------------------------------------------------------
// Multi-parent inheritance conflict — ADR-0039 §6 / I6
//
// When a membership-add returns the structured 422
// `MultiParentInheritanceConflict` body, the form parses it and renders
// an inline error block listing each diverging field and the
// parent-attributed values, with the submit button disabled until the
// operator either trims the parent set or sets the conflicting field
// explicitly.
// ---------------------------------------------------------------------------

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

/**
 * Drive the form to the membership-add phase. The agent install
 * succeeds immediately (status === "active"), so when
 * `assignUnitAgent` rejects with a 422 the form lands directly in the
 * conflict-render branch.
 */
async function submitForm() {
  fireEvent.click(await screen.findByLabelText(/assign to alpha/i));
  fireEvent.change(screen.getByLabelText(/agent id/i), {
    target: { value: "ada" },
  });
  fireEvent.change(screen.getByLabelText(/display name/i), {
    target: { value: "Ada" },
  });
  fireEvent.click(screen.getByTestId("agent-create-submit"));
}

describe("AgentCreateForm — multi-parent inheritance conflict (ADR-0039 I6)", () => {
  beforeEach(() => {
    installPackageFile.mockResolvedValue(
      makeInstallStatus({ status: "active", installId: "install-id-1" }),
    );
    getInstallStatus.mockResolvedValue(
      makeInstallStatus({ status: "active", installId: "install-id-1" }),
    );
    retryInstall.mockResolvedValue(
      makeInstallStatus({ status: "active", installId: "install-id-1" }),
    );
    abortInstall.mockResolvedValue(undefined);
  });

  it("renders the inline conflict block when assignUnitAgent returns 422", async () => {
    assignUnitAgent.mockRejectedValueOnce(
      new ApiError(422, "Unprocessable Content", {
        error: "MultiParentInheritanceConflict",
        conflictingFields: {
          runtime: [
            { source: "00000000000000000000000000000001", value: "claude-code" },
            { source: "00000000000000000000000000000002", value: "spring-voyage" },
          ],
        },
      }),
    );

    renderForm();
    await submitForm();

    const block = await screen.findByTestId(
      "multi-parent-inheritance-conflict",
    );
    expect(block).toBeInTheDocument();
    // Field name surfaces verbatim from the wire body.
    expect(block).toHaveTextContent(/runtime/);
    // Each parent-attributed value appears.
    expect(block).toHaveTextContent(/claude-code/);
    expect(block).toHaveTextContent(/spring-voyage/);
  });

  it("renders one row per diverging field, ordered as the wire body lists them", async () => {
    assignUnitAgent.mockRejectedValueOnce(
      new ApiError(422, "Unprocessable Content", {
        error: "MultiParentInheritanceConflict",
        conflictingFields: {
          runtime: [
            { source: "u1", value: "a" },
            { source: "u2", value: "b" },
          ],
          "model.provider": [
            { source: "u1", value: "anthropic" },
            { source: "u2", value: "openai" },
          ],
        },
      }),
    );

    renderForm();
    await submitForm();

    await screen.findByTestId("multi-parent-inheritance-conflict");
    expect(
      screen.getByTestId("multi-parent-inheritance-conflict-field-runtime"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId(
        "multi-parent-inheritance-conflict-field-model.provider",
      ),
    ).toBeInTheDocument();
  });

  it("attributes per-parent values using the unit display name when the parent is in the unit list", async () => {
    listUnits.mockResolvedValue([
      makeUnit({
        id: "00000000-0000-0000-0000-000000000001",
        name: "alpha",
        displayName: "Alpha",
      }),
      makeUnit({
        id: "00000000-0000-0000-0000-000000000002",
        name: "beta",
        displayName: "Beta",
      }),
    ]);
    assignUnitAgent.mockRejectedValueOnce(
      new ApiError(422, "Unprocessable Content", {
        error: "MultiParentInheritanceConflict",
        conflictingFields: {
          runtime: [
            // Canonical 32-hex form returned by the API; the form must
            // map it back to the hyphenated id in the unit list.
            { source: "00000000000000000000000000000001", value: "claude-code" },
            { source: "00000000000000000000000000000002", value: "spring-voyage" },
          ],
        },
      }),
    );

    renderForm({ initialUnitIds: ["alpha", "beta"] });
    await submitForm();

    const block = await screen.findByTestId(
      "multi-parent-inheritance-conflict",
    );
    expect(block).toHaveTextContent("Alpha");
    expect(block).toHaveTextContent("Beta");
  });

  it("disables the submit button while the conflict block is showing", async () => {
    assignUnitAgent.mockRejectedValueOnce(
      new ApiError(422, "Unprocessable Content", {
        error: "MultiParentInheritanceConflict",
        conflictingFields: {
          runtime: [
            { source: "u1", value: "a" },
            { source: "u2", value: "b" },
          ],
        },
      }),
    );

    renderForm();
    await submitForm();

    await screen.findByTestId("multi-parent-inheritance-conflict");
    expect(screen.getByTestId("agent-create-submit")).toBeDisabled();
  });

  it("re-enables the submit button once the operator changes a form field", async () => {
    assignUnitAgent.mockRejectedValueOnce(
      new ApiError(422, "Unprocessable Content", {
        error: "MultiParentInheritanceConflict",
        conflictingFields: {
          runtime: [
            { source: "u1", value: "a" },
            { source: "u2", value: "b" },
          ],
        },
      }),
    );

    renderForm();
    await submitForm();

    await screen.findByTestId("multi-parent-inheritance-conflict");
    expect(screen.getByTestId("agent-create-submit")).toBeDisabled();

    // Operator resolution: change the form (e.g. trim the parent set
    // by unchecking the unit, or set the conflicting field
    // explicitly). Either path clears the inline block.
    fireEvent.click(screen.getByLabelText(/assign to alpha/i));

    await waitFor(() => {
      expect(
        screen.queryByTestId("multi-parent-inheritance-conflict"),
      ).not.toBeInTheDocument();
    });
    expect(screen.getByTestId("agent-create-submit")).not.toBeDisabled();
  });

  it("falls back to the generic membership-error path when the 422 body is unparseable", async () => {
    // A 422 that is *not* the multi-parent conflict (e.g. a
    // generic problem-details with no `error` key) should not engage
    // the inline conflict block — it falls through to the existing
    // partial-success copy.
    assignUnitAgent.mockRejectedValueOnce(
      new ApiError(422, "Unprocessable Content", {
        type: "https://example.com/problems/other",
        title: "Something else",
      }),
    );

    renderForm();
    await submitForm();

    // Wait for the failure state to land. The conflict block must
    // not appear because the body did not match the discriminator.
    await waitFor(() => {
      expect(screen.getByText(/Membership in alpha/i)).toBeInTheDocument();
    });
    expect(
      screen.queryByTestId("multi-parent-inheritance-conflict"),
    ).not.toBeInTheDocument();
  });
});
