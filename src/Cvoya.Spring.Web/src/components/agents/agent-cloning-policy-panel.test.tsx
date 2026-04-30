import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

const useAgentCloningPolicyMock = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useAgentCloningPolicy: (id: string) => useAgentCloningPolicyMock(id),
}));

import { AgentCloningPolicyPanel } from "./agent-cloning-policy-panel";

describe("AgentCloningPolicyPanel (#534)", () => {
  const AGENT_ID = "agent-alpha";

  it("renders loading skeleton while fetching", () => {
    useAgentCloningPolicyMock.mockReturnValueOnce({
      data: undefined,
      isPending: true,
      isError: false,
    });
    render(<AgentCloningPolicyPanel agentId={AGENT_ID} />);
    expect(
      screen.getByTestId("agent-cloning-policy-loading"),
    ).toBeInTheDocument();
  });

  it("renders error state when fetch fails", () => {
    useAgentCloningPolicyMock.mockReturnValueOnce({
      data: null,
      isPending: false,
      isError: true,
    });
    render(<AgentCloningPolicyPanel agentId={AGENT_ID} />);
    expect(
      screen.getByTestId("agent-cloning-policy-error"),
    ).toBeInTheDocument();
  });

  it("renders empty-policy message (tenant default applies)", () => {
    useAgentCloningPolicyMock.mockReturnValueOnce({
      data: {
        allowedPolicies: null,
        allowedAttachmentModes: null,
        maxClones: null,
        maxDepth: null,
        budget: null,
      },
      isPending: false,
      isError: false,
    });
    render(<AgentCloningPolicyPanel agentId={AGENT_ID} />);
    expect(
      screen.getByTestId("agent-cloning-policy-panel"),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/tenant-wide default policy applies/),
    ).toBeInTheDocument();
  });

  it("renders constraint details when an agent policy is set", () => {
    useAgentCloningPolicyMock.mockReturnValueOnce({
      data: {
        allowedPolicies: ["ephemeral-no-memory"],
        allowedAttachmentModes: null,
        maxClones: 2,
        maxDepth: 0,
        budget: null,
      },
      isPending: false,
      isError: false,
    });
    render(<AgentCloningPolicyPanel agentId={AGENT_ID} />);
    expect(screen.getByText("ephemeral-no-memory")).toBeInTheDocument();
    expect(screen.getByText("2")).toBeInTheDocument();
    expect(screen.getByText("0 (no recursive cloning)")).toBeInTheDocument();
  });
});
