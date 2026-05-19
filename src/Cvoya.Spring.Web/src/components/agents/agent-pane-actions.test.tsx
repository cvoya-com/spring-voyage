/**
 * Tests for `AgentPaneActions` (#2372).
 *
 * Covers:
 *   - Status-gated button surface: Validate / Revalidate / Run / Stop
 *     each show on the matching LifecycleStatus; Delete is always shown.
 *   - Delete requires confirmation and only fires the mutation when the
 *     user explicitly confirms.
 *   - Engagement entry-point routes to the {human, agent} 1:1 surface.
 *
 * Mirrors the unit-side suite in
 * `src/Cvoya.Spring.Web/src/components/units/unit-pane-actions.test.tsx`
 * so future status-mapping changes have one shape to update on both
 * sides.
 */

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AgentNode } from "@/components/units/aggregate";
import type { AgentDetailResponse, LifecycleStatus } from "@/lib/api/types";

const routerReplaceMock = vi.fn();
const routerPushMock = vi.fn();
vi.mock("next/navigation", () => ({
  useRouter: () => ({
    replace: routerReplaceMock,
    push: routerPushMock,
  }),
}));

const deleteAgentMock = vi.fn();
const startAgentMock = vi.fn();
const stopAgentMock = vi.fn();
const revalidateAgentMock = vi.fn();

vi.mock("@/lib/api/client", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api/client")>(
    "@/lib/api/client",
  );
  return {
    ...actual,
    api: {
      deleteAgent: (id: string) => deleteAgentMock(id),
      startAgent: (id: string) => startAgentMock(id),
      stopAgent: (id: string) => stopAgentMock(id),
      revalidateAgent: (id: string) => revalidateAgentMock(id),
    },
  };
});

const useAgentMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useAgent: (id: string) => useAgentMock(id),
}));

function makeAgent(status: LifecycleStatus | null): AgentDetailResponse {
  return {
    agent: {
      id: "ada",
      name: "ada",
      displayName: "Ada",
      description: "",
      role: null,
      registeredAt: "2026-04-21T00:00:00Z",
      model: null,
      specialty: null,
      enabled: true,
      executionMode: "Auto",
      parentUnit: null,
      lifecycleStatus: status,
    },
    status: null,
    deployment: null,
  } as AgentDetailResponse;
}

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

import { AgentPaneActions } from "./agent-pane-actions";

function wrap(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

const agentNode: AgentNode = {
  kind: "Agent",
  id: "ada",
  name: "Ada",
  status: "running",
};

beforeEach(() => {
  routerReplaceMock.mockReset();
  routerPushMock.mockReset();
  deleteAgentMock.mockReset();
  startAgentMock.mockReset();
  stopAgentMock.mockReset();
  revalidateAgentMock.mockReset();
  useAgentMock.mockReset();
  // Default to "no data yet" so tests that don't care about the agent
  // detail query (engagement / chrome) don't crash with
  // `agentQuery is undefined`. Tests that need a concrete status
  // override this per-it.
  useAgentMock.mockReturnValue({ data: undefined });
  toastMock.mockReset();
});

describe("AgentPaneActions — Engagement (#1463 / #1464)", () => {
  it("navigates to /engagement/mine pre-selecting the agent", async () => {
    render(wrap(<AgentPaneActions node={agentNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("agent-action-engagement"));
    });
    expect(routerPushMock).toHaveBeenCalledWith(
      "/engagement/mine?agent=" + encodeURIComponent("ada"),
    );
  });

  it("uses the label 'Engagement' (not 'Start engagement')", () => {
    render(wrap(<AgentPaneActions node={agentNode} />));
    expect(screen.getByTestId("agent-action-engagement")).toHaveTextContent(
      /^\s*Engagement\s*$/,
    );
  });
});

describe("AgentPaneActions — chrome with no lifecycle data", () => {
  it("renders Engagement + Delete and no lifecycle verbs", () => {
    useAgentMock.mockReturnValue({ data: undefined });
    render(wrap(<AgentPaneActions node={agentNode} />));
    expect(screen.getByTestId("agent-action-delete")).toBeInTheDocument();
    expect(screen.getByTestId("agent-action-engagement")).toBeInTheDocument();
    // Without a known status no lifecycle verbs render.
    expect(screen.queryByTestId("agent-action-start")).toBeNull();
    expect(screen.queryByTestId("agent-action-stop")).toBeNull();
    expect(screen.queryByTestId("agent-action-validate")).toBeNull();
    expect(screen.queryByTestId("agent-action-revalidate")).toBeNull();
  });
});

// #2372: agent surface mirrors the unit verbs — Run / Stop / Revalidate
// / Validate, status-gated against the live LifecycleStatus read from
// `useAgent(id)`. Status-gating + dispatch coverage follows the
// unit-side test layout so future status-mapping changes have one
// place to update both sides.
describe("AgentPaneActions — status gating (#2372)", () => {
  const cases: Array<{
    status: LifecycleStatus;
    visible: string[];
    hidden: string[];
  }> = [
    {
      status: "Draft",
      visible: ["agent-action-validate", "agent-action-delete"],
      hidden: [
        "agent-action-revalidate",
        "agent-action-start",
        "agent-action-stop",
      ],
    },
    {
      status: "Stopped",
      visible: [
        "agent-action-revalidate",
        "agent-action-start",
        "agent-action-delete",
      ],
      hidden: ["agent-action-validate", "agent-action-stop"],
    },
    {
      status: "Running",
      visible: ["agent-action-stop", "agent-action-delete"],
      hidden: [
        "agent-action-validate",
        "agent-action-start",
        "agent-action-revalidate",
      ],
    },
    {
      status: "Error",
      visible: ["agent-action-revalidate", "agent-action-delete"],
      hidden: [
        "agent-action-validate",
        "agent-action-start",
        "agent-action-stop",
      ],
    },
    {
      status: "Validating",
      visible: ["agent-action-delete"],
      hidden: [
        "agent-action-validate",
        "agent-action-revalidate",
        "agent-action-start",
        "agent-action-stop",
      ],
    },
  ];

  for (const c of cases) {
    it(`renders the expected buttons for status="${c.status}"`, () => {
      useAgentMock.mockReturnValue({ data: makeAgent(c.status) });
      render(wrap(<AgentPaneActions node={agentNode} />));
      for (const id of c.visible) {
        expect(screen.getByTestId(id)).toBeInTheDocument();
      }
      for (const id of c.hidden) {
        expect(screen.queryByTestId(id)).toBeNull();
      }
    });
  }
});

describe("AgentPaneActions — Start / Stop / Revalidate dispatch (#2372)", () => {
  it("fires startAgent when Run is clicked on a Stopped agent", async () => {
    useAgentMock.mockReturnValue({ data: makeAgent("Stopped") });
    startAgentMock.mockResolvedValue({});
    render(wrap(<AgentPaneActions node={agentNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("agent-action-start"));
    });
    await waitFor(() => {
      expect(startAgentMock).toHaveBeenCalledWith("ada");
    });
  });

  it("fires stopAgent when Stop is clicked on a Running agent", async () => {
    useAgentMock.mockReturnValue({ data: makeAgent("Running") });
    stopAgentMock.mockResolvedValue({});
    render(wrap(<AgentPaneActions node={agentNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("agent-action-stop"));
    });
    await waitFor(() => {
      expect(stopAgentMock).toHaveBeenCalledWith("ada");
    });
  });

  it("fires revalidateAgent when Revalidate is clicked on an Error agent", async () => {
    useAgentMock.mockReturnValue({ data: makeAgent("Error") });
    revalidateAgentMock.mockResolvedValue({});
    render(wrap(<AgentPaneActions node={agentNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("agent-action-revalidate"));
    });
    await waitFor(() => {
      expect(revalidateAgentMock).toHaveBeenCalledWith("ada");
    });
  });

  it("fires revalidateAgent when Validate is clicked on a Draft agent", async () => {
    useAgentMock.mockReturnValue({ data: makeAgent("Draft") });
    revalidateAgentMock.mockResolvedValue({});
    render(wrap(<AgentPaneActions node={agentNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("agent-action-validate"));
    });
    await waitFor(() => {
      expect(revalidateAgentMock).toHaveBeenCalledWith("ada");
    });
  });
});

describe("AgentPaneActions — Delete confirmation flow", () => {
  it("requires confirmation before firing deleteAgent", async () => {
    useAgentMock.mockReturnValue({ data: makeAgent("Stopped") });
    deleteAgentMock.mockResolvedValue(undefined);
    render(wrap(<AgentPaneActions node={agentNode} />));
    fireEvent.click(screen.getByTestId("agent-action-delete"));
    expect(deleteAgentMock).not.toHaveBeenCalled();

    await act(async () => {
      fireEvent.click(
        screen.getByRole("button", { name: /permanently delete/i }),
      );
    });
    await waitFor(() => {
      expect(deleteAgentMock).toHaveBeenCalledWith("ada");
    });
    await waitFor(() => {
      expect(routerReplaceMock).toHaveBeenCalledWith("/explorer");
    });
  });

  it("cancels without calling deleteAgent", () => {
    useAgentMock.mockReturnValue({ data: makeAgent("Stopped") });
    render(wrap(<AgentPaneActions node={agentNode} />));
    fireEvent.click(screen.getByTestId("agent-action-delete"));
    fireEvent.click(screen.getByRole("button", { name: /cancel/i }));
    expect(deleteAgentMock).not.toHaveBeenCalled();
  });
});
