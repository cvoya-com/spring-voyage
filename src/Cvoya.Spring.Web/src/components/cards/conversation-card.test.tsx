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

  it("links into the first unit/agent participant's Messages tab (issue #937)", () => {
    render(
      <ConversationCard
        conversation={{
          id: "conv-1",
          title: "PR review thread",
          participants: ["human://alice", "agent://ada"],
        }}
      />,
    );
    expect(screen.getByTestId("conversation-open-conv-1")).toHaveAttribute(
      "href",
      "/units?node=ada&tab=Messages&conversation=conv-1",
    );
  });

  it("falls back to /inbox when no unit/agent participant is available", () => {
    render(
      <ConversationCard
        conversation={{
          id: "conv-2",
          title: "Human-only thread",
          participants: ["human://alice", "human://bob"],
        }}
      />,
    );
    expect(screen.getByTestId("conversation-open-conv-2")).toHaveAttribute(
      "href",
      "/inbox?conversation=conv-2",
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
    // With no unit/agent participant the link falls back to inbox.
    expect(link).toHaveAttribute("href", "/inbox?conversation=conv-1");
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

  // v2 design-system reskin (CARD-conversation-refresh, #851): the
  // participant list is mono-typed, the status pill uses the status
  // badge variant, and the last-activity timestamp is a pill.
  it("renders participants as a mono-font address list", () => {
    render(
      <ConversationCard
        conversation={{
          id: "conv-1",
          participants: ["agent://ada"],
        }}
      />,
    );
    const list = screen.getByTestId("conversation-participants");
    expect(list.className).toMatch(/font-mono/);
  });

  it("renders the last-activity timestamp as a pill badge", () => {
    render(
      <ConversationCard
        conversation={{
          id: "conv-1",
          lastActivityAt: "2026-04-01T00:00:00Z",
        }}
      />,
    );
    const pill = screen.getByTestId("conversation-last-activity");
    expect(pill.tagName).toBe("SPAN");
    expect(pill.className).toMatch(/rounded-full/);
  });

  it("renders the conversation id under the title in mono font", () => {
    render(
      <ConversationCard
        conversation={{ id: "conv-1", title: "PR review thread" }}
      />,
    );
    const idLine = screen.getByText("conv-1");
    expect(idLine.className).toMatch(/font-mono/);
  });
});
