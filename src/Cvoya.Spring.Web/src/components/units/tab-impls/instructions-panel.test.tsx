import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type {
  AgentDetailResponse,
  UnitResponse,
} from "@/lib/api/types";

const getAgent = vi.fn<(id: string) => Promise<AgentDetailResponse>>();
const getUnit = vi.fn<(id: string) => Promise<UnitResponse>>();
const updateAgentMetadata =
  vi.fn<(id: string, patch: { instructions: string | null }) => Promise<void>>();
const updateUnit =
  vi.fn<(id: string, patch: { instructions: string | null }) => Promise<void>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getAgent: (id: string) => getAgent(id),
    getUnit: (id: string) => getUnit(id),
    updateAgentMetadata: (
      id: string,
      patch: { instructions: string | null },
    ) => updateAgentMetadata(id, patch),
    updateUnit: (id: string, patch: { instructions: string | null }) =>
      updateUnit(id, patch),
  },
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

import { InstructionsPanel } from "./instructions-panel";

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function buildAgent(instructions: string | null): AgentDetailResponse {
  return {
    agent: {
      id: "00000000000000000000000000000001",
      name: "00000000000000000000000000000001",
      displayName: "Ada",
      description: "Test agent",
      role: "reviewer",
      registeredAt: "2026-01-01T00:00:00Z",
      enabled: true,
      executionMode: "auto",
      parentUnit: "Engineering",
      instructions,
    },
    status: null,
  } as unknown as AgentDetailResponse;
}

function buildUnit(instructions: string | null): UnitResponse {
  return {
    id: "00000000000000000000000000000010",
    name: "00000000000000000000000000000010",
    displayName: "Engineering",
    description: "Builds stuff",
    registeredAt: "2026-01-01T00:00:00Z",
    status: "draft",
    model: null,
    color: null,
    instructions,
  } as unknown as UnitResponse;
}

describe("InstructionsPanel", () => {
  beforeEach(() => {
    getAgent.mockReset();
    getUnit.mockReset();
    updateAgentMetadata.mockReset();
    updateUnit.mockReset();
    toastMock.mockReset();
  });

  it("Agent: renders empty when the agent has no instructions and no parent", async () => {
    getAgent.mockResolvedValue(buildAgent(null));

    render(
      <Wrapper>
        <InstructionsPanel
          kind="Agent"
          id="00000000000000000000000000000001"
          parentUnitId={null}
        />
      </Wrapper>,
    );

    const textarea = (await screen.findByTestId(
      "agent-instructions-textarea",
    )) as HTMLTextAreaElement;
    expect(textarea.value).toBe("");
    expect(
      screen.queryByTestId("agent-instructions-inherited"),
    ).not.toBeInTheDocument();
  });

  it("Agent: seeds the textarea from the persisted value", async () => {
    getAgent.mockResolvedValue(buildAgent("Write tight code."));

    render(
      <Wrapper>
        <InstructionsPanel
          kind="Agent"
          id="00000000000000000000000000000001"
          parentUnitId={null}
        />
      </Wrapper>,
    );

    const textarea = (await screen.findByTestId(
      "agent-instructions-textarea",
    )) as HTMLTextAreaElement;
    await waitFor(() => expect(textarea.value).toBe("Write tight code."));
  });

  it("Agent: surfaces the parent unit's instructions as an inherited overlay", async () => {
    getAgent.mockResolvedValue(buildAgent(null));
    getUnit.mockResolvedValue(buildUnit("Be helpful."));

    render(
      <Wrapper>
        <InstructionsPanel
          kind="Agent"
          id="00000000000000000000000000000001"
          parentUnitId="00000000000000000000000000000010"
        />
      </Wrapper>,
    );

    await screen.findByTestId("agent-instructions-panel");
    await waitFor(() =>
      expect(getUnit).toHaveBeenCalledWith("00000000000000000000000000000010"),
    );
    const overlay = await screen.findByTestId(
      "agent-instructions-inherited",
    );
    expect(overlay.textContent).toContain("Be helpful.");
  });

  it("Agent: Save sends the textarea contents to updateAgentMetadata", async () => {
    getAgent.mockResolvedValue(buildAgent(null));
    updateAgentMetadata.mockResolvedValue(undefined);

    render(
      <Wrapper>
        <InstructionsPanel
          kind="Agent"
          id="00000000000000000000000000000001"
          parentUnitId={null}
        />
      </Wrapper>,
    );

    const textarea = (await screen.findByTestId(
      "agent-instructions-textarea",
    )) as HTMLTextAreaElement;
    fireEvent.change(textarea, { target: { value: "New rules." } });
    fireEvent.click(screen.getByTestId("agent-instructions-save"));

    await waitFor(() =>
      expect(updateAgentMetadata).toHaveBeenCalledWith(
        "00000000000000000000000000000001",
        { instructions: "New rules." },
      ),
    );
  });

  it("Unit: Save sends the new value to updateUnit", async () => {
    getUnit.mockResolvedValue(buildUnit(null));
    updateUnit.mockResolvedValue(undefined);

    render(
      <Wrapper>
        <InstructionsPanel
          kind="Unit"
          id="00000000000000000000000000000010"
        />
      </Wrapper>,
    );

    const textarea = (await screen.findByTestId(
      "unit-instructions-textarea",
    )) as HTMLTextAreaElement;
    fireEvent.change(textarea, { target: { value: "Be precise." } });
    fireEvent.click(screen.getByTestId("unit-instructions-save"));

    await waitFor(() =>
      expect(updateUnit).toHaveBeenCalledWith(
        "00000000000000000000000000000010",
        { instructions: "Be precise." },
      ),
    );
  });

  it("Unit: Clear sends null to updateUnit", async () => {
    getUnit.mockResolvedValue(buildUnit("Old text"));
    updateUnit.mockResolvedValue(undefined);

    render(
      <Wrapper>
        <InstructionsPanel
          kind="Unit"
          id="00000000000000000000000000000010"
        />
      </Wrapper>,
    );

    await screen.findByTestId("unit-instructions-textarea");
    fireEvent.click(screen.getByTestId("unit-instructions-clear"));

    await waitFor(() =>
      expect(updateUnit).toHaveBeenCalledWith(
        "00000000000000000000000000000010",
        { instructions: null },
      ),
    );
  });
});
