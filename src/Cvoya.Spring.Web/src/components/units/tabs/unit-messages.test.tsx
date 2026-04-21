import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

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

import UnitMessagesTab from "./unit-messages";

describe("UnitMessagesTab", () => {
  const node: UnitNode = {
    kind: "Unit",
    id: "engineering",
    name: "Engineering",
    status: "running",
  };

  it("renders the empty state when no conversations", () => {
    useConversationsMock.mockReturnValueOnce({
      data: [],
      isLoading: false,
      error: null,
    });
    render(<UnitMessagesTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-unit-messages-empty")).toHaveTextContent(
      "No conversations",
    );
  });

  it("filters conversations by unit id", () => {
    useConversationsMock.mockReturnValueOnce({
      data: [
        {
          id: "abc",
          summary: "Ada asks about build",
          lastActivity: "2026-04-20T00:00:00Z",
          status: "open",
        },
      ],
      isLoading: false,
      error: null,
    });
    render(<UnitMessagesTab node={node} path={[node]} />);
    expect(useConversationsMock).toHaveBeenCalledWith({
      unit: "engineering",
    });
    expect(screen.getByText("Ada asks about build")).toBeInTheDocument();
  });
});
