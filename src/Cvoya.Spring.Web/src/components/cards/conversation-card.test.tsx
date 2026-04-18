import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ConversationCard } from "./conversation-card";

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

describe("ConversationCard", () => {
  it("renders title, participants, status, and last-activity", () => {
    render(
      <ConversationCard
        conversation={{
          id: "conv-1",
          title: "PR review thread",
          participants: ["human://alice", "agent://ada"],
          status: "open",
          lastActivityAt: "2026-04-01T00:00:00Z",
        }}
      />,
    );
    expect(screen.getByText("PR review thread")).toBeInTheDocument();
    expect(screen.getByTestId("conversation-participants")).toHaveTextContent(
      "human://alice",
    );
    expect(screen.getByTestId("conversation-status-badge")).toHaveTextContent(
      "open",
    );
    expect(screen.getByTestId("conversation-last-activity")).toBeInTheDocument();
  });

  it("links to /conversations/[id] even though the route is not yet live", () => {
    render(
      <ConversationCard
        conversation={{
          id: "conv-1",
          title: "PR review thread",
        }}
      />,
    );
    expect(screen.getByTestId("conversation-open-conv-1")).toHaveAttribute(
      "href",
      "/conversations/conv-1",
    );
  });

  it("falls back gracefully when the conversation is nearly empty", () => {
    render(<ConversationCard conversation={{ id: "conv-empty" }} />);
    // Falls back to "Conversation <id>" title.
    expect(screen.getByText("Conversation conv-empty")).toBeInTheDocument();
    expect(
      screen.getByTestId("conversation-participants-empty"),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("conversation-status-badge")).toBeNull();
    expect(screen.queryByTestId("conversation-last-activity")).toBeNull();
  });

  it("exposes a full-card primary link that navigates to the conversation (#593)", () => {
    render(
      <ConversationCard
        conversation={{
          id: "conv-1",
          title: "PR review thread",
        }}
      />,
    );
    const link = screen.getByTestId("conversation-card-link-conv-1");
    expect(link).toHaveAttribute("href", "/conversations/conv-1");
    expect(link).toHaveAttribute(
      "aria-label",
      "Open conversation PR review thread",
    );
    expect(link.className).toMatch(/after:absolute/);
    expect(link.className).toMatch(/after:inset-0/);
  });

  it("truncates participants over 3 with a '+N more' marker", () => {
    render(
      <ConversationCard
        conversation={{
          id: "conv-many",
          participants: [
            "human://a",
            "human://b",
            "agent://c",
            "agent://d",
            "agent://e",
          ],
        }}
      />,
    );
    expect(screen.getByTestId("conversation-participants")).toHaveTextContent(
      "+2 more",
    );
  });
});
