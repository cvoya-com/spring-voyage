import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import { LifecyclePanel } from "./lifecycle-panel";

// Hoist-safe mock surface. One vi.mock per module; methods resolve to
// fresh vi.fns on each test via beforeEach so expectations don't leak.
const mockGetDeployment = vi.fn();
const mockDeploy = vi.fn();
const mockUndeploy = vi.fn();
const mockScale = vi.fn();
const mockGetLogs = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    getPersistentAgentDeployment: (...a: unknown[]) =>
      mockGetDeployment(...a),
    deployPersistentAgent: (...a: unknown[]) => mockDeploy(...a),
    undeployPersistentAgent: (...a: unknown[]) => mockUndeploy(...a),
    scalePersistentAgent: (...a: unknown[]) => mockScale(...a),
    getPersistentAgentLogs: (...a: unknown[]) => mockGetLogs(...a),
  },
}));

// Silence the toast surface during tests — we assert on API calls, not on
// toast UI, which is covered by its own tests.
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

const emptyDeployment = {
  agentId: "agent-1",
  running: false,
  healthStatus: "unknown",
  replicas: 0,
  image: null,
  endpoint: null,
  containerId: null,
  startedAt: null,
  consecutiveFailures: 0,
};

const runningDeployment = {
  agentId: "agent-1",
  running: true,
  healthStatus: "healthy",
  replicas: 1,
  image: "ghcr.io/cvoya-com/spring-agent:2.1.98",
  endpoint: "http://agent-1:8080",
  containerId: "abc1234567890def",
  startedAt: new Date().toISOString(),
  consecutiveFailures: 0,
};

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe("LifecyclePanel", () => {
  beforeEach(() => {
    mockGetDeployment.mockReset();
    mockDeploy.mockReset();
    mockUndeploy.mockReset();
    mockScale.mockReset();
    mockGetLogs.mockReset();
    mockGetDeployment.mockResolvedValue(emptyDeployment);
    mockDeploy.mockResolvedValue(runningDeployment);
    mockUndeploy.mockResolvedValue(emptyDeployment);
    mockScale.mockResolvedValue(runningDeployment);
    mockGetLogs.mockResolvedValue({
      agentId: "agent-1",
      containerId: "abc1234567890def",
      tail: 200,
      logs: "line 1\nline 2\n",
    });
  });

  it("renders the not-deployed empty state by default", async () => {
    render(
      <Wrapper>
        <LifecyclePanel agentId="agent-1" />
      </Wrapper>,
    );

    expect(
      await screen.findByTestId("agent-lifecycle-running-badge"),
    ).toHaveTextContent(/not deployed/i);
    // The details block only renders when running — assert it's absent.
    expect(
      screen.queryByTestId("agent-lifecycle-details"),
    ).not.toBeInTheDocument();
  });

  it("deploy button calls the API without an image override when the field is empty", async () => {
    render(
      <Wrapper>
        <LifecyclePanel agentId="agent-1" />
      </Wrapper>,
    );
    fireEvent.click(screen.getByTestId("agent-lifecycle-deploy"));
    await waitFor(() => {
      expect(mockDeploy).toHaveBeenCalledWith("agent-1", undefined);
    });
  });

  it("deploy button forwards the image override when set", async () => {
    render(
      <Wrapper>
        <LifecyclePanel agentId="agent-1" />
      </Wrapper>,
    );
    const image = "ghcr.io/cvoya-com/spring-agent:latest";
    fireEvent.change(screen.getByTestId("agent-lifecycle-image-input"), {
      target: { value: image },
    });
    fireEvent.click(screen.getByTestId("agent-lifecycle-deploy"));
    await waitFor(() => {
      expect(mockDeploy).toHaveBeenCalledWith("agent-1", { image });
    });
  });

  it("undeploy + scale buttons hit their respective verbs 1:1 with the CLI", async () => {
    render(
      <Wrapper>
        <LifecyclePanel agentId="agent-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByTestId("agent-lifecycle-undeploy"));
    await waitFor(() => {
      expect(mockUndeploy).toHaveBeenCalledWith("agent-1");
    });

    fireEvent.click(screen.getByTestId("agent-lifecycle-scale-up"));
    await waitFor(() => {
      expect(mockScale).toHaveBeenCalledWith("agent-1", { replicas: 1 });
    });

    fireEvent.click(screen.getByTestId("agent-lifecycle-scale-zero"));
    await waitFor(() => {
      expect(mockScale).toHaveBeenCalledWith("agent-1", { replicas: 0 });
    });
  });

  it("renders the running details block and health badge when deployed", async () => {
    render(
      <Wrapper>
        <LifecyclePanel
          agentId="agent-1"
          initialDeployment={runningDeployment}
        />
      </Wrapper>,
    );
    // Badge flips to Running and the details block is present.
    expect(
      await screen.findByTestId("agent-lifecycle-running-badge"),
    ).toHaveTextContent(/running/i);
    expect(screen.getByTestId("agent-lifecycle-details")).toBeInTheDocument();
    expect(
      screen.getByText("ghcr.io/cvoya-com/spring-agent:2.1.98"),
    ).toBeInTheDocument();
  });

  it("logs panel lazy-loads on toggle and respects the tail input", async () => {
    render(
      <Wrapper>
        <LifecyclePanel agentId="agent-1" />
      </Wrapper>,
    );

    // No logs call before the panel is opened.
    expect(mockGetLogs).not.toHaveBeenCalled();

    fireEvent.click(screen.getByTestId("agent-lifecycle-logs-toggle"));
    await waitFor(() => {
      expect(mockGetLogs).toHaveBeenCalledWith("agent-1", 200);
    });

    // Change the tail value; the query should refetch with the new key.
    fireEvent.change(screen.getByTestId("agent-lifecycle-tail-input"), {
      target: { value: "50" },
    });
    await waitFor(() => {
      expect(mockGetLogs).toHaveBeenCalledWith("agent-1", 50);
    });

    expect(
      await screen.findByTestId("agent-lifecycle-logs-pane"),
    ).toHaveTextContent("line 1");
  });
});
