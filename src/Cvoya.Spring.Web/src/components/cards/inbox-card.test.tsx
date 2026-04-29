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
  threadId: "conv-42",
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

  it("deep-links 'Open thread' back to /inbox with the conversation id", () => {
    render(<InboxCard item={baseItem} />);
    const link = screen.getByTestId("inbox-open-conv-42");
    expect(link).toHaveAttribute("href", "/inbox?thread=conv-42");
  });

  it("links agent:// senders to the Explorer Overview tab", () => {
    render(<InboxCard item={baseItem} />);
    const link = screen.getByTestId("inbox-from-link-conv-42");
    expect(link).toHaveAttribute(
      "href",
      "/units?node=engineering-team%2Fada&tab=Overview",
    );
  });

  it("links unit:// senders to the Explorer node", () => {
    render(
      <InboxCard
        item={{ ...baseItem, from: "unit://engineering-team" }}
      />,
    );
    const link = screen.getByTestId("inbox-from-link-conv-42");
    expect(link).toHaveAttribute("href", "/units?node=engineering-team");
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

  it("exposes a full-card primary link that navigates to the inbox with the conversation id (#593)", () => {
    render(<InboxCard item={baseItem} />);
    const link = screen.getByTestId("inbox-card-link-conv-42");
    expect(link).toHaveAttribute("href", "/inbox?thread=conv-42");
    expect(link).toHaveAttribute(
      "aria-label",
      "Open conversation Need your call on the migration plan",
    );
    expect(link.className).toMatch(/after:absolute/);
    expect(link.className).toMatch(/after:inset-0/);
  });

  it("renders the 'Awaiting you' status badge", () => {
    render(<InboxCard item={baseItem} />);
    expect(screen.getByTestId("inbox-status-badge")).toHaveTextContent(
      "Awaiting you",
    );
  });

  // v2 design-system reskin (CARD-inbox-refresh, #850): the `from://`
  // header is mono-typed, the pendingSince timestamp is a pill, and
  // the card surface uses the shared `bg-card` + `border-border`
  // tokens. Assert markup, not raw Tailwind class strings.
  it("renders the `from://` header in Geist mono", () => {
    render(<InboxCard item={baseItem} />);
    const fromRow = screen.getByTestId("inbox-from");
    expect(fromRow.className).toMatch(/font-mono/);
  });

  it("renders the pendingSince timestamp as a pill badge", () => {
    render(<InboxCard item={baseItem} />);
    const pendingSince = screen.getByTestId("inbox-pending-since");
    expect(pendingSince).toHaveTextContent(/ago/);
    // Badge primitive renders as a `<span>` with the pill class set.
    expect(pendingSince.tagName).toBe("SPAN");
    expect(pendingSince.className).toMatch(/rounded-full/);
  });
});
