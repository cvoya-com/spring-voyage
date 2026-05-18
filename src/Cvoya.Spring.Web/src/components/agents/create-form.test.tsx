import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  AgentResponse,
  InstallStatusResponse,
  InstalledModelProviderResponse,
  PackageDetail,
  PackageSummary,
  ProviderCredentialStatusResponse,
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
const getProviderCredentialStatus =
  vi.fn<
    (
      provider: string,
      authMethod?: string,
    ) => Promise<ProviderCredentialStatusResponse>
  >();
const createAgent = vi.fn();
const installPackages = vi.fn();
const listPackages = vi.fn();
const getPackage = vi.fn();
const listConnectorBindings = vi.fn();

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
      getProviderCredentialStatus: (
        provider: string,
        _agentImage?: string,
        authMethod?: string,
      ) => getProviderCredentialStatus(provider, authMethod),
      getUnitExecution: (id: string) => getUnitExecution(id),
      createAgent: (body: unknown) => createAgent(body),
      installPackages: (targets: unknown) => installPackages(targets),
      listPackages: () => listPackages(),
      getPackage: (name: string) => getPackage(name),
      listConnectorBindings: (slugOrId: string) =>
        listConnectorBindings(slugOrId),
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

function makeAgent(overrides: Partial<AgentResponse> = {}): AgentResponse {
  return {
    id: overrides.id ?? "00000000-0000-0000-0000-0000000000ad",
    name: overrides.name ?? "ada",
    displayName: overrides.displayName ?? "Ada",
    description: overrides.description ?? "",
    role: overrides.role ?? null,
    registeredAt: overrides.registeredAt ?? new Date().toISOString(),
    model: overrides.model ?? null,
    specialty: overrides.specialty ?? null,
    enabled: overrides.enabled ?? true,
    executionMode: overrides.executionMode ?? "Respond",
    parentUnit: overrides.parentUnit ?? null,
    hostingMode: overrides.hostingMode ?? null,
    initiativeLevel: overrides.initiativeLevel ?? null,
  } as AgentResponse;
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
    humanTemplateCount: overrides.humanTemplateCount ?? 0,
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
    humanTemplates: overrides.humanTemplates ?? [],
    connectorDeclarations: overrides.connectorDeclarations ?? [],
    content: overrides.content ?? [],
    execution: overrides.execution ?? null,
  } as PackageDetail;
}

function makeInstallStatus(
  overrides: Partial<InstallStatusResponse> = {},
): InstallStatusResponse {
  const now = new Date().toISOString();
  return {
    installId:
      overrides.installId ?? "00000000-0000-0000-0000-000000000189",
    status: overrides.status ?? "active",
    packages: overrides.packages ?? [],
    startedAt: overrides.startedAt ?? now,
    completedAt: overrides.completedAt ?? now,
    error: overrides.error ?? null,
  } as InstallStatusResponse;
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
  getProviderCredentialStatus.mockResolvedValue({
    provider: "anthropic",
    resolvable: true,
    source: "tenant",
    suggestion: null,
  });
  createAgent.mockResolvedValue(makeAgent());
  installPackages.mockResolvedValue(makeInstallStatus());
  listPackages.mockResolvedValue([]);
  getPackage.mockResolvedValue(makePackageDetail());
  listConnectorBindings.mockResolvedValue([]);
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
// ADR-0039 K3 — from-package connector requirements panel.
// ---------------------------------------------------------------------------

describe("AgentCreateForm — package connector requirements (K3)", () => {
  it("shows connector requirements after selecting a package that declares them", async () => {
    listPackages.mockResolvedValue([
      makePackageSummary({ name: "software-agents", agentTemplateCount: 2 }),
    ]);
    getPackage.mockResolvedValue(
      makePackageDetail({
        name: "software-agents",
        connectorDeclarations: [{ type: "github", required: true }],
      }),
    );
    listConnectorBindings.mockResolvedValue([
      {
        unitId: "binding-123",
        unitName: "engineering",
        unitDisplayName: "Engineering Team",
        typeId: "type-github",
        typeSlug: "github",
        configUrl: "/api/v1/tenant/connectors/github/units/engineering/config",
        actionsBaseUrl: "/api/v1/tenant/connectors/github/actions",
      },
    ]);

    renderForm({ context: "page" });

    fireEvent.click(screen.getByTestId("agent-source-card-from-package"));
    fireEvent.click(screen.getByRole("button", { name: /next/i }));
    fireEvent.click(
      await screen.findByTestId("package-picker-item-software-agents"),
    );

    await screen.findByText("Connector requirements");
    const panel = screen.getByTestId("package-connector-requirements");
    expect(panel).toHaveTextContent("Connector requirements");
    expect(panel).toHaveTextContent("github");
    expect(panel).toHaveTextContent(/Choose an existing binding/i);
    expect(
      await screen.findByTestId("package-connector-binding-github"),
    ).toBeInTheDocument();
  });

  it("does not show connector requirements for a package without declarations", async () => {
    listPackages.mockResolvedValue([
      makePackageSummary({ name: "plain-agents", agentTemplateCount: 1 }),
    ]);
    getPackage.mockResolvedValue(
      makePackageDetail({
        name: "plain-agents",
        connectorDeclarations: [],
      }),
    );

    renderForm({ context: "page" });

    fireEvent.click(screen.getByTestId("agent-source-card-from-package"));
    fireEvent.click(screen.getByRole("button", { name: /next/i }));
    fireEvent.click(
      await screen.findByTestId("package-picker-item-plain-agents"),
    );

    await waitFor(() => {
      expect(getPackage).toHaveBeenCalledWith("plain-agents");
    });
    await waitFor(() => {
      expect(
        screen.queryByTestId("package-connector-requirements"),
      ).not.toBeInTheDocument();
    });
  });
});

async function chooseFromPackage(packageName = "agent-pack") {
  fireEvent.click(screen.getByTestId("agent-source-card-from-package"));
  fireEvent.click(screen.getByRole("button", { name: /next/i }));
  fireEvent.click(await screen.findByTestId(`package-picker-item-${packageName}`));
}

// ---------------------------------------------------------------------------
// ADR-0039 K4 — from-package submit posts to package install.
// ---------------------------------------------------------------------------

describe("AgentCreateForm — from-package submit (K4)", () => {
  it("posts the selected package, inputs, and connector binding selections to installPackages", async () => {
    listPackages.mockResolvedValue([
      makePackageSummary({ name: "software-agents", agentTemplateCount: 2 }),
    ]);
    getPackage.mockResolvedValue(
      makePackageDetail({
        name: "software-agents",
        connectorDeclarations: [{ type: "github", required: true }],
      }),
    );
    listConnectorBindings.mockResolvedValue([
      {
        unitId: "binding-123",
        unitName: "engineering",
        unitDisplayName: "Engineering Team",
        typeId: "type-github",
        typeSlug: "github",
        configUrl: "/api/v1/tenant/connectors/github/units/engineering/config",
        actionsBaseUrl: "/api/v1/tenant/connectors/github/actions",
      },
    ]);

    renderForm({ context: "page" });
    await chooseFromPackage("software-agents");

    fireEvent.change(
      await screen.findByTestId("package-connector-binding-github"),
      { target: { value: "binding-123" } },
    );
    fireEvent.click(screen.getByTestId("package-picker-confirm"));

    await screen.findByText("Package selected");
    fireEvent.click(screen.getByTestId("agent-create-submit"));

    await waitFor(() => {
      expect(installPackages).toHaveBeenCalledTimes(1);
    });
    expect(installPackages).toHaveBeenCalledWith([
      {
        packageName: "software-agents",
        inputs: {},
        connectorBindings: {
          package: {
            github: {
              config: { bindingId: "binding-123" },
            },
          },
          units: null,
        },
      },
    ]);
    expect(createAgent).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// ADR-0039 K5 — manifest-derived success copy.
// ---------------------------------------------------------------------------

describe("AgentCreateForm — from-package success copy (K5)", () => {
  it("renders unit install success when the selected package declares units", async () => {
    listPackages.mockResolvedValue([
      makePackageSummary({ name: "unit-pack", agentTemplateCount: 1 }),
    ]);
    getPackage.mockResolvedValue(
      makePackageDetail({
        name: "unit-pack",
        unitTemplates: [
          {
            package: "unit-pack",
            name: "engineering-team",
            description: null,
            path: "units/engineering-team.yaml",
          },
        ],
      }),
    );

    renderForm({ context: "page" });
    await chooseFromPackage("unit-pack");
    fireEvent.click(screen.getByTestId("package-picker-confirm"));

    await screen.findByText("Package selected");
    fireEvent.click(screen.getByTestId("agent-create-submit"));

    expect(await screen.findByTestId("agent-create-success")).toHaveTextContent(
      "Unit installed successfully.",
    );
  });

  it("renders agent create success when the selected package has no units", async () => {
    listPackages.mockResolvedValue([
      makePackageSummary({ name: "agent-pack", agentTemplateCount: 1 }),
    ]);
    getPackage.mockResolvedValue(
      makePackageDetail({
        name: "agent-pack",
        agentTemplates: [
          {
            package: "agent-pack",
            name: "ada",
            displayName: "Ada",
            role: "reviewer",
            description: null,
            path: "agents/ada.yaml",
          },
        ],
      }),
    );

    renderForm({ context: "page" });
    await chooseFromPackage("agent-pack");
    fireEvent.click(screen.getByTestId("package-picker-confirm"));

    await screen.findByText("Package selected");
    fireEvent.click(screen.getByTestId("agent-create-submit"));

    expect(await screen.findByTestId("agent-create-success")).toHaveTextContent(
      "Agent created successfully.",
    );
  });

  it("falls back to generic install success when the manifest is unavailable", async () => {
    listPackages.mockResolvedValue([
      makePackageSummary({ name: "offline-pack", agentTemplateCount: 1 }),
    ]);
    getPackage.mockRejectedValue(new Error("manifest unavailable"));

    renderForm({ context: "page" });
    await chooseFromPackage("offline-pack");
    fireEvent.click(screen.getByTestId("package-picker-confirm"));

    await screen.findByText("Package selected");
    fireEvent.click(screen.getByTestId("agent-create-submit"));

    expect(await screen.findByTestId("agent-create-success")).toHaveTextContent(
      "Installed successfully.",
    );
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
// ADR-0039 K6 — scratch path posts directly to POST /api/v1/tenant/agents.
// ---------------------------------------------------------------------------

describe("AgentCreateForm — direct create submit (K6)", () => {
  it("posts a tenant-parented agent with unitIds [] and definitionJson null", async () => {
    const onSuccess = vi.fn();
    renderForm({ onSuccess });

    fireEvent.change(screen.getByLabelText(/agent id/i), {
      target: { value: "ada" },
    });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada" },
    });
    fireEvent.click(screen.getByTestId("agent-create-submit"));

    await waitFor(() => {
      expect(createAgent).toHaveBeenCalledTimes(1);
    });
    expect(createAgent).toHaveBeenCalledWith({
      displayName: "Ada",
      description: "",
      role: null,
      unitIds: [],
      definitionJson: null,
    });
    expect(onSuccess).toHaveBeenCalledWith({
      agentId: "00000000-0000-0000-0000-0000000000ad",
      unitIds: [],
    });
  });

  it("posts selected unit ids and the direct definitionJson shape", async () => {
    renderForm();

    fireEvent.click(await screen.findByLabelText(/assign to alpha/i));
    fireEvent.change(screen.getByLabelText(/agent id/i), {
      target: { value: "ada" },
    });
    fireEvent.change(screen.getByLabelText(/display name/i), {
      target: { value: "Ada Lovelace" },
    });
    fireEvent.change(screen.getByLabelText(/^role$/i), {
      target: { value: "reviewer" },
    });
    fireEvent.change(screen.getByLabelText(/description/i), {
      target: { value: "Reviews changes" },
    });
    fireEvent.change(screen.getByLabelText(/agent runtime/i), {
      target: { value: "claude-code" },
    });
    fireEvent.change(screen.getByLabelText(/container image/i), {
      target: { value: "ghcr.io/example/agent:latest" },
    });
    fireEvent.change(screen.getByLabelText(/hosting mode/i), {
      target: { value: "persistent" },
    });

    fireEvent.click(screen.getByTestId("agent-create-submit"));

    await waitFor(() => {
      expect(createAgent).toHaveBeenCalledTimes(1);
    });
    expect(createAgent).toHaveBeenCalledWith({
      displayName: "Ada Lovelace",
      description: "Reviews changes",
      role: "reviewer",
      unitIds: ["unit-id-alpha"],
      definitionJson: JSON.stringify({
        runtime: "claude-code",
        execution: {
          image: "ghcr.io/example/agent:latest",
          hosting: "persistent",
        },
      }),
    });
  });

  it("shows Claude Code OAuth token status for the inherited default runtime", async () => {
    getProviderCredentialStatus.mockResolvedValue({
      provider: "anthropic",
      resolvable: false,
      source: null,
      suggestion: null,
    });

    renderForm();

    const status = await screen.findByTestId("agent-create-credential-status");
    expect(status).toHaveTextContent("Claude Code OAuth token");
    expect(status).toHaveTextContent("not configured");
    await waitFor(() => {
      expect(getProviderCredentialStatus).toHaveBeenCalledWith(
        "anthropic",
        "oauth",
      );
    });
  });

  it.each([
    ["codex", "openai", "OpenAI API key"],
    ["gemini", "google", "Google API key"],
    ["spring-voyage", "anthropic", "Anthropic API key"],
  ])(
    "uses API-key credential status for %s + %s",
    async (runtime, provider, label) => {
      listModelProviders.mockResolvedValue([
        makeProvider({
          id: "anthropic",
          displayName: "Anthropic",
        }),
        makeProvider({
          id: "openai",
          displayName: "OpenAI",
          models: ["gpt-4o"],
          defaultModel: "gpt-4o",
          credentialSecretName: "openai-api-key",
        }),
        makeProvider({
          id: "google",
          displayName: "Google",
          models: ["gemini-2.5-pro"],
          defaultModel: "gemini-2.5-pro",
          credentialSecretName: "google-api-key",
        }),
      ]);
      getProviderCredentialStatus.mockResolvedValue({
        provider,
        resolvable: false,
        source: null,
        suggestion: null,
      });

      renderForm();
      fireEvent.change(await screen.findByLabelText(/agent runtime/i), {
        target: { value: runtime },
      });
      if (runtime === "spring-voyage") {
        fireEvent.change(await screen.findByLabelText(/model provider/i), {
          target: { value: provider },
        });
      }

      await waitFor(() => {
        expect(screen.getByTestId("agent-create-credential-status"))
          .toHaveTextContent(label);
      });
      await waitFor(() => {
        expect(getProviderCredentialStatus).toHaveBeenCalledWith(
          provider,
          "api-key",
        );
      });
    },
  );
});

// ---------------------------------------------------------------------------
// ADR-0039 I4 — per-field inherit affordance on Execution fields
// (DESIGN.md §12.6).
// ---------------------------------------------------------------------------

describe("AgentCreateForm — inherit affordance (I4)", () => {
  it("renders `inherit-indicator` for every visible Execution field while defaulting to the fixed-provider runtime", async () => {
    renderForm();

    // The default inherited runtime is `claude-code`, whose provider is fixed
    // to Anthropic. The model-provider field is legitimately hidden in this
    // state, leaving four visible inheritable controls.
    await waitFor(() => {
      const indicators = screen.getAllByTestId("inherit-indicator");
      expect(indicators).toHaveLength(4);
    });
    expect(screen.queryByLabelText(/model provider/i)).not.toBeInTheDocument();
  });

  it("renders all five inherit indicators when the inherited runtime is multi-provider", async () => {
    listUnits.mockResolvedValue([
      makeUnit({ name: "engineering", displayName: "Engineering Team" }),
    ]);
    getUnitExecution.mockResolvedValue({
      image: null,
      runtime: "spring-voyage",
      model: null,
    } as UnitExecutionResponse);

    renderForm();

    fireEvent.click(await screen.findByLabelText(/assign to engineering team/i));

    await waitFor(() => {
      expect(getUnitExecution).toHaveBeenCalledWith("engineering");
    });
    await waitFor(() => {
      expect(screen.getByLabelText(/model provider/i)).toBeInTheDocument();
      expect(screen.getAllByTestId("inherit-indicator")).toHaveLength(5);
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

  it("uses generic inherited-from-parent copy for multiple selected units", async () => {
    listUnits.mockResolvedValue([
      makeUnit({ id: "unit-id-alpha", name: "alpha", displayName: "Alpha" }),
      makeUnit({ id: "unit-id-beta", name: "beta", displayName: "Beta" }),
    ]);

    renderForm();

    fireEvent.click(await screen.findByLabelText(/assign to alpha/i));
    fireEvent.click(await screen.findByLabelText(/assign to beta/i));

    await waitFor(() => {
      const texts = screen
        .getAllByTestId("inherit-indicator")
        .map((el) => el.textContent ?? "");
      expect(texts.some((text) => text === "inherited from parent")).toBe(true);
      expect(texts.every((text) => !text.includes("Alpha:"))).toBe(true);
      expect(texts.every((text) => !text.includes("Beta:"))).toBe(true);
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
// When direct create returns the structured 422
// `MultiParentInheritanceConflict` body, the form parses it and renders
// an inline error block listing each diverging field and the
// parent-attributed values, with the submit button disabled until the
// operator either trims the parent set or sets the conflicting field
// explicitly.
// ---------------------------------------------------------------------------

/**
 * Drive the form to the direct create call. When `createAgent` rejects
 * with a structured 422, the form lands in the conflict-render branch.
 */
async function submitForm({ selectAlpha = true }: { selectAlpha?: boolean } = {}) {
  if (selectAlpha) {
    fireEvent.click(await screen.findByLabelText(/assign to alpha/i));
  }
  fireEvent.change(screen.getByLabelText(/agent id/i), {
    target: { value: "ada" },
  });
  fireEvent.change(screen.getByLabelText(/display name/i), {
    target: { value: "Ada" },
  });
  fireEvent.click(screen.getByTestId("agent-create-submit"));
}

describe("AgentCreateForm — multi-parent inheritance conflict (ADR-0039 I6)", () => {
  it("renders the inline conflict block when createAgent returns 422", async () => {
    createAgent.mockRejectedValueOnce(
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
    expect(toastMock).not.toHaveBeenCalled();
  });

  it("renders one row per diverging field, ordered as the wire body lists them", async () => {
    createAgent.mockRejectedValueOnce(
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
    createAgent.mockRejectedValueOnce(
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
    await submitForm({ selectAlpha: false });

    const block = await screen.findByTestId(
      "multi-parent-inheritance-conflict",
    );
    expect(block).toHaveTextContent("Alpha");
    expect(block).toHaveTextContent("Beta");
  });

  it("disables the submit button while the conflict block is showing", async () => {
    createAgent.mockRejectedValueOnce(
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
    createAgent.mockRejectedValueOnce(
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

  it("falls back to the generic API-error path when the 422 body is unparseable", async () => {
    // A 422 that is *not* the multi-parent conflict (e.g. a
    // generic problem-details with no `error` key) should not engage
    // the inline conflict block — it falls through to the generic API
    // error copy.
    createAgent.mockRejectedValueOnce(
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
      expect(screen.getByTestId("agent-create-error")).toHaveTextContent(
        /something else/i,
      );
    });
    expect(
      screen.queryByTestId("multi-parent-inheritance-conflict"),
    ).not.toBeInTheDocument();
  });
});
