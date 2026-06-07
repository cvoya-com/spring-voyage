import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

// Mock the embedded expertise panel so this test stays focused on the
// metadata edit + save plumbing.
vi.mock("@/components/expertise/agent-expertise-panel", () => ({
  AgentExpertisePanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="legacy-agent-expertise" data-agent-id={agentId}>
      Agent expertise
    </div>
  ),
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

const useAgentMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useAgent: (id: string) => useAgentMock(id),
}));

const updateAgentMetadataMock = vi.fn();
vi.mock("@/lib/api/client", () => ({
  api: {
    updateAgentMetadata: (...args: unknown[]) =>
      updateAgentMetadataMock(...args),
  },
}));

import { AgentGeneralPanel } from "./agent-general-panel";

function withClient(ui: ReactNode): ReactNode {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return <QueryClientProvider client={client}>{ui}</QueryClientProvider>;
}

describe("AgentGeneralPanel (#2331)", () => {
  beforeEach(() => {
    useAgentMock.mockReset();
    updateAgentMetadataMock.mockReset();
  });

  it("seeds the form from the persisted agent metadata", () => {
    useAgentMock.mockReturnValue({
      isPending: false,
      data: {
        agent: {
          displayName: "Ada",
          description: "Senior engineer",
          role: "backend-engineer",
          specialty: "reviewer",
          enabled: true,
          executionMode: "Auto",
        },
      },
    });
    render(withClient(<AgentGeneralPanel agentId="ada" />));

    expect(
      (screen.getByTestId("agent-general-display-name") as HTMLInputElement)
        .value,
    ).toBe("Ada");
    expect(
      (screen.getByTestId("agent-general-role") as HTMLInputElement).value,
    ).toBe("backend-engineer");
    expect(
      (screen.getByTestId("agent-general-specialty") as HTMLInputElement)
        .value,
    ).toBe("reviewer");
    expect(
      (screen.getByTestId("agent-general-execution-mode") as HTMLSelectElement)
        .value,
    ).toBe("Auto");
    expect(
      (screen.getByTestId("agent-general-enabled") as HTMLInputElement)
        .checked,
    ).toBe(true);
  });

  it("renders the expertise editor inline under the metadata card", () => {
    useAgentMock.mockReturnValue({
      isPending: false,
      data: {
        agent: {
          displayName: "Ada",
          description: "",
          role: "",
          specialty: "",
          enabled: true,
          executionMode: "Auto",
        },
      },
    });
    render(withClient(<AgentGeneralPanel agentId="ada" />));

    const expertise = screen.getByTestId("legacy-agent-expertise");
    expect(expertise.dataset.agentId).toBe("ada");
  });

  it("sends only the dirty fields on save (role + enabled toggle)", async () => {
    useAgentMock.mockReturnValue({
      isPending: false,
      data: {
        agent: {
          displayName: "Ada",
          description: "Senior engineer",
          role: "backend-engineer",
          specialty: "",
          enabled: true,
          executionMode: "Auto",
        },
      },
    });
    updateAgentMetadataMock.mockResolvedValue(undefined);

    render(withClient(<AgentGeneralPanel agentId="ada" />));

    fireEvent.change(screen.getByTestId("agent-general-role"), {
      target: { value: "frontend-engineer" },
    });
    fireEvent.click(screen.getByTestId("agent-general-enabled"));

    fireEvent.click(screen.getByTestId("agent-general-save"));

    await waitFor(() => {
      expect(updateAgentMetadataMock).toHaveBeenCalledWith("ada", {
        role: "frontend-engineer",
        enabled: false,
      });
    });
  });

  it("sends executionMode when the select changes", async () => {
    useAgentMock.mockReturnValue({
      isPending: false,
      data: {
        agent: {
          displayName: "Ada",
          description: "",
          role: "",
          specialty: "",
          enabled: true,
          executionMode: "Auto",
        },
      },
    });
    updateAgentMetadataMock.mockResolvedValue(undefined);

    render(withClient(<AgentGeneralPanel agentId="ada" />));

    fireEvent.change(screen.getByTestId("agent-general-execution-mode"), {
      target: { value: "OnDemand" },
    });

    fireEvent.click(screen.getByTestId("agent-general-save"));

    await waitFor(() => {
      expect(updateAgentMetadataMock).toHaveBeenCalledWith("ada", {
        executionMode: "OnDemand",
      });
    });
  });

  it("renders the loading skeleton while the agent detail query is pending", () => {
    useAgentMock.mockReturnValue({ isPending: true, data: undefined });
    render(withClient(<AgentGeneralPanel agentId="ada" />));
    expect(screen.getByTestId("agent-general-skeleton")).toBeInTheDocument();
  });
});
