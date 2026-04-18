import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { InboxCard } from "./inbox-card";

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

const baseItem = {
  conversationId: "conv-42",
  from: "agent://engineering-team/ada",
  human: "human://savas",
  pendingSince: new Date(Date.now() - 1000 * 60 * 5).toISOString(),
  summary: "Need your call on the migration plan",
};

describe("InboxCard", () => {
  it("renders summary, from address, and pendingSince", () => {
    render(<InboxCard item={baseItem} />);
    expect(
      screen.getByText("Need your call on the migration plan"),
    ).toBeInTheDocument();
    expect(
      screen.getByText("agent://engineering-team/ada"),
    ).toBeInTheDocument();
    expect(screen.getByText(/m ago/)).toBeInTheDocument();
  });

  it("deep-links 'Open thread' to /conversations/{id}", () => {
    render(<InboxCard item={baseItem} />);
    const link = screen.getByTestId("inbox-open-conv-42");
    expect(link).toHaveAttribute("href", "/conversations/conv-42");
  });

  it("links agent:// senders to the agent detail page", () => {
    render(<InboxCard item={baseItem} />);
    const link = screen.getByTestId("inbox-from-link-conv-42");
    expect(link).toHaveAttribute(
      "href",
      "/agents/engineering-team%2Fada",
    );
  });

  it("links unit:// senders to the unit detail page", () => {
    render(
      <InboxCard
        item={{ ...baseItem, from: "unit://engineering-team" }}
      />,
    );
    const link = screen.getByTestId("inbox-from-link-conv-42");
    expect(link).toHaveAttribute("href", "/units/engineering-team");
  });

  it("does not link human:// senders (no portal detail page)", () => {
    render(
      <InboxCard item={{ ...baseItem, from: "human://another-user" }} />,
    );
    expect(
      screen.queryByTestId("inbox-from-link-conv-42"),
    ).not.toBeInTheDocument();
    expect(screen.getByText("human://another-user")).toBeInTheDocument();
  });

  it("falls back to the conversation id when summary is empty", () => {
    render(<InboxCard item={{ ...baseItem, summary: "" }} />);
    // conversation id appears twice: once as title fallback and once
    // as the muted meta row.
    expect(screen.getAllByText("conv-42").length).toBeGreaterThan(0);
  });

  it("renders the 'Awaiting you' status badge", () => {
    render(<InboxCard item={baseItem} />);
    expect(screen.getByTestId("inbox-status-badge")).toHaveTextContent(
      "Awaiting you",
    );
  });
});
