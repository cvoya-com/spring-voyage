import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

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

const useConversationsMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useConversations: (filters: unknown) => useConversationsMock(filters),
}));

import AgentMessagesTab from "./agent-messages";

describe("AgentMessagesTab", () => {
  const node: AgentNode = {
    kind: "Agent",
    id: "ada",
    name: "Ada",
    status: "running",
  };

  it("filters conversations by agent id", () => {
    useConversationsMock.mockReturnValueOnce({
      data: [],
      isLoading: false,
      error: null,
    });
    render(<AgentMessagesTab node={node} path={[node]} />);
    expect(useConversationsMock).toHaveBeenCalledWith({ agent: "ada" });
    expect(screen.getByTestId("tab-agent-messages-empty")).toBeInTheDocument();
  });
});
