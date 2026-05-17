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
  from: { id: "22222222-2222-2222-2222-222222222222", address: "agent://engineering-team/ada", displayName: "engineering-team/ada" },
  human: { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
  pendingSince: new Date(Date.now() - 1000 * 60 * 5).toISOString(),
  summary: "Need your call on the migration plan",
  unreadCount: 0,
};

describe("InboxCard", () => {
  it("renders summary, from address, and pendingSince", () => {
    render(<InboxCard item={baseItem} />);
    expect(
      screen.getByText("Need your call on the migration plan"),
    ).toBeInTheDocument();
    expect(
      screen.getByText("engineering-team/ada"),
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
        item={{ ...baseItem, from: { id: "44444444-4444-4444-4444-444444444444", address: "unit://engineering-team", displayName: "engineering-team" } }}
      />,
    );
    const link = screen.getByTestId("inbox-from-link-conv-42");
    expect(link).toHaveAttribute("href", "/units?node=engineering-team");
  });

  it("does not link human:// senders (no portal detail page)", () => {
    render(
      <InboxCard item={{ ...baseItem, from: { id: "99999999-9999-9999-9999-999999999999", address: "human://another-user", displayName: "another-user" } }} />,
    );
    expect(
      screen.queryByTestId("inbox-from-link-conv-42"),
    ).not.toBeInTheDocument();
    expect(screen.getByText("another-user")).toBeInTheDocument();
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

  // PR #2390 fixed the agent/unit footer rows; #2441 extends the same
  // pattern to every overlay-link card. Non-overlay rows must carry
  // `pointer-events-none` so whitespace clicks fall through to the
  // overlay link; interactive descendants (the `from://` link, the
  // footer Open-thread link) restore `pointer-events-auto`.
  describe("click-gap regression (#2441)", () => {
    it("marks the from row + footer row as pointer-events-none and keeps interactive children clickable", () => {
      render(<InboxCard item={baseItem} />);

      // From row wrapper carries `pointer-events-none`; the inner
      // `from://` link restores `pointer-events-auto`.
      const fromLink = screen.getByTestId("inbox-from-link-conv-42");
      expect(fromLink.closest(".pointer-events-none")).not.toBeNull();
      expect(fromLink.className).toMatch(/pointer-events-auto/);

      // Footer row wrapper carries `pointer-events-none`; the
      // `Open thread` link restores `pointer-events-auto`.
      const open = screen.getByTestId("inbox-open-conv-42");
      expect(open.closest(".pointer-events-none")).not.toBeNull();
      expect(open.className).toMatch(/pointer-events-auto/);
    });
  });
});
