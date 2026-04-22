import {
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  AgentResponse,
  CreateAgentRequest,
  InstalledAgentRuntimeResponse,
  UnitResponse,
} from "@/lib/api/types";

// Mocks. The standalone agent-create page reads four endpoints:
//   - api.listUnits     (initial-assignment picker)
//   - api.listAgentRuntimes / api.getAgentRuntimeModels (model dropdown)
//   - api.createAgent   (submit)
const listUnits = vi.fn();
const listAgentRuntimes = vi.fn();
const getAgentRuntimeModels = vi.fn();
const createAgent = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    listUnits: () => listUnits(),
    listAgentRuntimes: () => listAgentRuntimes(),
    getAgentRuntimeModels: (id: string) => getAgentRuntimeModels(id),
    createAgent: (body: unknown) => createAgent(body),
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
    tool: overrides.tool ?? null,
    provider: overrides.provider ?? null,
    hosting: overrides.hosting ?? null,
  } as UnitResponse;
}

function makeRuntime(
  overrides: Partial<InstalledAgentRuntimeResponse> = {},
): InstalledAgentRuntimeResponse {
  const now = new Date().toISOString();
  return {
    id: overrides.id ?? "claude",
    displayName: overrides.displayName ?? "Claude",
    toolKind: overrides.toolKind ?? "claude-code",
    installedAt: overrides.installedAt ?? now,
    updatedAt: overrides.updatedAt ?? now,
    models: overrides.models ?? ["claude-3-5-sonnet"],
    defaultModel: overrides.defaultModel ?? "claude-3-5-sonnet",
    baseUrl: overrides.baseUrl ?? null,
    credentialKind: overrides.credentialKind ?? "ApiKey",
    credentialDisplayHint: overrides.credentialDisplayHint ?? null,
    credentialSecretName:
      overrides.credentialSecretName ?? "anthropic-api-key",
  } as InstalledAgentRuntimeResponse;
}

function makeAgent(overrides: Partial<AgentResponse> = {}): AgentResponse {
  return {
    id: overrides.id ?? "agent-id-1",
    name: overrides.name ?? "ada",
    displayName: overrides.displayName ?? "Ada",
    description: overrides.description ?? "",
    role: overrides.role ?? null,
    registeredAt: overrides.registeredAt ?? new Date().toISOString(),
    model: overrides.model ?? null,
    specialty: overrides.specialty ?? null,
    enabled: overrides.enabled ?? true,
    executionMode: overrides.executionMode ?? "Auto",
    parentUnit: overrides.parentUnit ?? "alpha",
  } as AgentResponse;
}

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

beforeEach(() => {
  vi.clearAllMocks();
  listUnits.mockResolvedValue([makeUnit()]);
  listAgentRuntimes.mockResolvedValue([makeRuntime()]);
  getAgentRuntimeModels.mockResolvedValue([
    { id: "claude-3-5-sonnet", displayName: "Claude 3.5 Sonnet" },
  ]);
  createAgent.mockResolvedValue(
    makeAgent({ name: "ada", displayName: "Ada" }),
  );
});

describe("CreateAgentPage", () => {
  it("renders the form with id, displayName, role, execution and unit picker", async () => {
    renderPage();

    expect(
      screen.getByRole("heading", { name: /create a new agent/i }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText(/agent id/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/display name/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/^role$/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/execution tool/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/container image/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/container runtime/i)).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByLabelText(/assign to alpha/i)).toBeInTheDocument();
    });
  });

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

    fireEvent.click(
      screen.getByRole("button", { name: /create agent/i }),
    );

    await waitFor(() => {
      expect(screen.getByTestId("agent-create-error")).toHaveTextContent(
        /pick at least one unit/i,
      );
    });
    expect(createAgent).not.toHaveBeenCalled();
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
    fireEvent.click(
      screen.getByRole("button", { name: /create agent/i }),
    );

    await waitFor(() => {
      expect(screen.getByTestId("agent-create-error")).toHaveTextContent(
        /url-safe/i,
      );
    });
    expect(createAgent).not.toHaveBeenCalled();
  });

  it("submits the wire body and redirects to the unit's Agents tab on success", async () => {
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
    fireEvent.change(screen.getByLabelText(/container runtime/i), {
      target: { value: "docker" },
    });

    fireEvent.click(
      screen.getByRole("button", { name: /create agent/i }),
    );

    await waitFor(() => {
      expect(createAgent).toHaveBeenCalledTimes(1);
    });
    const body = createAgent.mock.calls[0][0] as CreateAgentRequest;
    expect(body.name).toBe("ada");
    expect(body.displayName).toBe("Ada Lovelace");
    expect(body.role).toBe("reviewer");
    expect(body.unitIds).toEqual(["alpha"]);
    expect(body.definitionJson).toBeTruthy();
    const def = JSON.parse(body.definitionJson as string) as {
      execution: Record<string, string>;
    };
    expect(def.execution.image).toBe("ghcr.io/example/agent:latest");
    expect(def.execution.runtime).toBe("docker");
    expect(def.execution.tool).toBe("claude-code");

    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith(
        "/units?node=alpha&tab=Agents",
      );
    });
  });

  it("surfaces an API error message inline (4xx)", async () => {
    createAgent.mockRejectedValueOnce(
      new Error("Agent ada already exists in this tenant."),
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
    fireEvent.click(
      screen.getByRole("button", { name: /create agent/i }),
    );

    await waitFor(() => {
      expect(screen.getByTestId("agent-create-error")).toHaveTextContent(
        /already exists/i,
      );
    });
    expect(pushMock).not.toHaveBeenCalled();
  });
});
