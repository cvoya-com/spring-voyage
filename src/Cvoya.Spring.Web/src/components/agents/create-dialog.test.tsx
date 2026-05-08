import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  InstalledModelProviderResponse,
  PackageDetail,
  PackageSummary,
  UnitResponse,
} from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Mocks — parallel surface to `create-form.test.tsx` so the dialog can
// render the wrapped form without hitting the network. The dialog itself
// makes no API calls; the mocks exist purely to satisfy `<AgentCreateForm>`.
// ---------------------------------------------------------------------------

const listUnits = vi.fn();
const listModelProviders = vi.fn();
const getModelProviderModels = vi.fn();
const getUnitExecution = vi.fn();
const createAgent = vi.fn();
const listPackages = vi.fn();
const getPackage = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    listUnits: () => listUnits(),
    listModelProviders: () => listModelProviders(),
    getModelProviderModels: (id: string) => getModelProviderModels(id),
    getUnitExecution: (id: string) => getUnitExecution(id),
    createAgent: (body: unknown) => createAgent(body),
    listPackages: () => listPackages(),
    getPackage: (name: string) => getPackage(name),
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

import { AgentCreateDialog } from "./create-dialog";

// ---------------------------------------------------------------------------
// Factory helpers
// ---------------------------------------------------------------------------

function makeUnit(overrides: Partial<UnitResponse> = {}): UnitResponse {
  return {
    id: overrides.id ?? "unit-id-engineering",
    name: overrides.name ?? "engineering",
    displayName: overrides.displayName ?? "Engineering Team",
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

function makePackageSummary(
  overrides: Partial<PackageSummary> = {},
): PackageSummary {
  return {
    name: overrides.name ?? "agent-pack",
    description: overrides.description ?? "Agent package",
    unitTemplateCount: overrides.unitTemplateCount ?? 0,
    agentTemplateCount: overrides.agentTemplateCount ?? 1,
    skillCount: overrides.skillCount ?? 0,
    connectorCount: overrides.connectorCount ?? 0,
    workflowCount: overrides.workflowCount ?? 0,
    version: overrides.version ?? null,
  } as PackageSummary;
}

function makePackageDetail(
  overrides: Partial<PackageDetail> = {},
): PackageDetail {
  return {
    name: overrides.name ?? "agent-pack",
    description: overrides.description ?? "Agent package",
    readme: overrides.readme ?? null,
    version: overrides.version ?? null,
    unitTemplates: overrides.unitTemplates ?? [],
    agentTemplates: overrides.agentTemplates ?? [],
    skills: overrides.skills ?? [],
    connectors: overrides.connectors ?? [],
    workflows: overrides.workflows ?? [],
    connectorDeclarations: overrides.connectorDeclarations ?? [],
    content: overrides.content ?? [],
    execution: overrides.execution ?? null,
  } as PackageDetail;
}

function renderDialog(
  props: Partial<Parameters<typeof AgentCreateDialog>[0]> = {},
) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
  }
  const finalProps: Parameters<typeof AgentCreateDialog>[0] = {
    unitId: props.unitId ?? "engineering",
    unitDisplayName: props.unitDisplayName ?? "Engineering Team",
    open: props.open ?? true,
    onOpenChange: props.onOpenChange ?? vi.fn(),
  };
  return {
    ...render(<AgentCreateDialog {...finalProps} />, { wrapper: Wrapper }),
    props: finalProps,
  };
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
  getUnitExecution.mockResolvedValue({
    image: null,
    runtime: null,
    model: null,
  });
  createAgent.mockResolvedValue({});
  listPackages.mockResolvedValue([makePackageSummary()]);
  getPackage.mockResolvedValue(makePackageDetail());
  // Reset body scroll-lock between tests (the underlying <Dialog> sets
  // `overflow: hidden` while open — see `dialog.test.tsx`).
  document.body.style.overflow = "";
});

// ---------------------------------------------------------------------------
// Tests — ADR-0039 J1 acceptance:
//   1. The dialog renders with the unit display name in the header.
//   2. The form receives `unitId` as preselected (`initialUnitIds`).
// ---------------------------------------------------------------------------

describe("AgentCreateDialog — J1 shell", () => {
  it("renders nothing when `open` is false", () => {
    renderDialog({ open: false });
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("renders the dialog with the unit display name in the header", () => {
    renderDialog({
      unitId: "engineering",
      unitDisplayName: "Engineering Team",
    });

    const dialog = screen.getByRole("dialog");
    // Accessible name combines the title; aria-labelledby points at the
    // <h2> the underlying <Dialog> renders.
    expect(dialog).toHaveAccessibleName("Create agent in Engineering Team");
    // Description copy mirrors design §2.5 — names the unit, signals
    // inherited execution defaults.
    expect(dialog).toHaveAccessibleDescription(
      /registered in Engineering Team and inherits its execution defaults/i,
    );
  });

  it("renders the unit display name, not the unit id, in the header copy", () => {
    renderDialog({
      unitId: "unit-guid-123",
      unitDisplayName: "Engineering Team",
    });

    const dialog = screen.getByRole("dialog");
    const titleId = dialog.getAttribute("aria-labelledby");
    const descriptionId = dialog.getAttribute("aria-describedby");
    const title = titleId ? document.getElementById(titleId) : null;
    const description = descriptionId
      ? document.getElementById(descriptionId)
      : null;

    expect(title).toHaveTextContent("Create agent in Engineering Team");
    expect(description).toHaveTextContent(
      "This agent will be registered in Engineering Team",
    );
    expect(title).not.toHaveTextContent("unit-guid-123");
    expect(description).not.toHaveTextContent("unit-guid-123");
    expect(`${title?.textContent ?? ""} ${description?.textContent ?? ""}`)
      .not.toMatch(
        /(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/i,
      );
  });

  it("updates the header copy when unitDisplayName changes", () => {
    const onOpenChange = vi.fn();
    const { rerender } = renderDialog({
      unitId: "engineering",
      unitDisplayName: "Engineering Team",
      onOpenChange,
    });

    expect(screen.getByRole("dialog")).toHaveAccessibleName(
      "Create agent in Engineering Team",
    );

    rerender(
      <AgentCreateDialog
        unitId="engineering"
        unitDisplayName="Platform Operations"
        open
        onOpenChange={onOpenChange}
      />,
    );

    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveAccessibleName("Create agent in Platform Operations");
    expect(dialog).toHaveAccessibleDescription(
      /registered in Platform Operations and inherits its execution defaults/i,
    );
  });

  it("renders the unit confirmation strip with the display name and address", () => {
    renderDialog({
      unitId: "engineering",
      unitDisplayName: "Engineering Team",
    });

    const strip = screen.getByTestId("agent-create-dialog-unit-strip");
    expect(strip).toHaveTextContent("Engineering Team");
    expect(strip).toHaveTextContent("unit://engineering");
  });

  it("preselects the unit in the wrapped form via initialUnitIds", async () => {
    // The form's unit-assignment fieldset renders a checkbox per unit
    // returned by `listUnits()`. The dialog passes `[unitId]` to
    // `initialUnitIds`, which the form uses to seed `form.unitIds` —
    // the matching checkbox should already be checked on first render.
    renderDialog({
      unitId: "engineering",
      unitDisplayName: "Engineering Team",
    });

    await waitFor(() => {
      expect(
        screen.getByLabelText(/assign to engineering team/i),
      ).toBeInTheDocument();
    });
    const checkbox = screen.getByLabelText(
      /assign to engineering team/i,
    ) as HTMLInputElement;
    expect(checkbox.checked).toBe(true);
  });

  it("shows the From package link in scratch mode", () => {
    renderDialog({
      unitId: "engineering",
      unitDisplayName: "Engineering Team",
    });

    expect(
      screen.getByTestId("agent-create-dialog-from-package-link"),
    ).toHaveTextContent("From package");
  });

  it("opens the from-package branch when the footer link is clicked", async () => {
    renderDialog({
      unitId: "engineering",
      unitDisplayName: "Engineering Team",
    });

    fireEvent.click(
      screen.getByTestId("agent-create-dialog-from-package-link"),
    );

    await waitFor(() => {
      expect(screen.getByTestId("package-picker-list")).toBeInTheDocument();
    });
    expect(
      screen.queryByTestId("agent-create-dialog-from-package-link"),
    ).not.toBeInTheDocument();
  });

  it("calls onOpenChange(false) when the form is cancelled", async () => {
    const onOpenChange = vi.fn();
    renderDialog({
      unitId: "engineering",
      unitDisplayName: "Engineering Team",
      onOpenChange,
    });

    // The form renders a `Cancel` button that fires `onCancel`. The
    // dialog wires that to `onOpenChange(false)` so the unit-tab parent
    // closes the dialog without owning the form's cancel state.
    const cancel = await screen.findByRole("button", { name: /cancel/i });
    cancel.click();
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });
});
