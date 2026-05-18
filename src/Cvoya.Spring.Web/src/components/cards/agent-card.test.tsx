import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { AgentCard } from "./agent-card";

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

describe("AgentCard", () => {
  it("renders agent display name, role, and open link", () => {
    render(
      <AgentCard
        agent={{
          name: "ada",
          displayName: "Ada",
          role: "backend",
          registeredAt: "2026-04-01T00:00:00Z",
        }}
      />,
    );
    expect(screen.getByText("Ada")).toBeInTheDocument();
    expect(screen.getByTestId("agent-role-badge")).toHaveTextContent("backend");
    expect(screen.getByTestId("agent-open-ada")).toHaveAttribute(
      "href",
      "/units?node=ada&tab=Overview",
    );
  });

  it("links to parent unit, conversations, and cost detail", () => {
    render(
      <AgentCard
        agent={{
          name: "engineering/ada",
          displayName: "Ada",
          role: "backend",
          registeredAt: "2026-04-01T00:00:00Z",
          parentUnit: "engineering",
          // #2372: agent cards now render the shared 7-state lifecycle
          // badge (Draft/Validating/Stopped/Starting/Running/Stopping/
          // Error). The legacy `"active"/"idle"/"busy"/"error"` projection
          // is gone — pass the canonical lifecycle status instead.
          status: "Running",
          executionMode: "Auto",
        }}
      />,
    );

    expect(screen.getByTestId("agent-parent-unit")).toHaveAttribute(
      "href",
      "/units?node=engineering",
    );
    expect(
      screen.getByTestId("agent-link-conversations-engineering/ada"),
    ).toHaveAttribute("href", "/units?node=engineering%2Fada&tab=Messages");
    expect(
      screen.getByTestId("agent-link-cost-engineering/ada"),
    ).toHaveAttribute("href", "/units?node=engineering%2Fada&tab=Overview");
    expect(screen.getByTestId("agent-execution-mode-badge")).toHaveTextContent(
      "Auto",
    );
    expect(screen.getByTestId("agent-status-badge")).toHaveTextContent(
      "Running",
    );
  });

  it("renders without role, parent-unit, or last-activity when absent", () => {
    render(
      <AgentCard
        agent={{
          name: "ada",
          displayName: "Ada",
          role: null,
          registeredAt: "2026-04-01T00:00:00Z",
        }}
      />,
    );
    expect(screen.getByText("Ada")).toBeInTheDocument();
    expect(screen.queryByTestId("agent-role-badge")).toBeNull();
    expect(screen.queryByTestId("agent-parent-unit")).toBeNull();
    expect(screen.queryByTestId("agent-last-activity")).toBeNull();
  });

  it("renders an `actions` slot in the footer when provided (#472)", () => {
    render(
      <AgentCard
        agent={{
          name: "ada",
          displayName: "Ada",
          role: null,
          registeredAt: "2026-04-01T00:00:00Z",
        }}
        actions={
          <button data-testid="custom-action" type="button">
            Edit
          </button>
        }
      />,
    );
    expect(screen.getByTestId("agent-actions-ada")).toBeInTheDocument();
    expect(screen.getByTestId("custom-action")).toBeInTheDocument();
    // Open affordance must coexist alongside the quick actions.
    expect(screen.getByTestId("agent-open-ada")).toBeInTheDocument();
  });

  it("does not render the actions wrapper when no actions are provided", () => {
    render(
      <AgentCard
        agent={{
          name: "ada",
          displayName: "Ada",
          role: null,
          registeredAt: "2026-04-01T00:00:00Z",
        }}
      />,
    );
    expect(screen.queryByTestId("agent-actions-ada")).toBeNull();
  });

  it("exposes a full-card primary link that navigates to the agent detail (#593)", () => {
    render(
      <AgentCard
        agent={{
          name: "ada",
          displayName: "Ada",
          role: null,
          registeredAt: "2026-04-01T00:00:00Z",
        }}
      />,
    );
    const link = screen.getByTestId("agent-card-link-ada");
    expect(link).toHaveAttribute("href", "/units?node=ada&tab=Overview");
    expect(link).toHaveAttribute("aria-label", "Open agent Ada");
    // The full-card overlay is delivered via the ::after pseudo; the
    // stylesheet-level assertion is covered by the axe smoke tests.
    expect(link.className).toMatch(/after:absolute/);
    expect(link.className).toMatch(/after:inset-0/);
  });

  it("accepts explicit parentUnit and lastActivity props as overrides", () => {
    render(
      <AgentCard
        agent={{
          name: "ada",
          displayName: "Ada",
          role: null,
          registeredAt: "2026-04-01T00:00:00Z",
        }}
        parentUnit="engineering"
        lastActivity="Replied to PR review"
      />,
    );
    expect(screen.getByTestId("agent-parent-unit")).toHaveTextContent(
      "engineering",
    );
    expect(screen.getByTestId("agent-last-activity")).toHaveTextContent(
      "Replied to PR review",
    );
  });

  it("renders a CardTabRow footer and hides the legacy cross-links when onOpenTab is provided", () => {
    const onOpenTab = vi.fn();
    render(
      <AgentCard
        agent={{
          name: "ada",
          displayName: "Ada",
          role: null,
          registeredAt: "2026-04-01T00:00:00Z",
        }}
        onOpenTab={onOpenTab}
      />,
    );

    expect(screen.queryByTestId("agent-link-conversations-ada")).toBeNull();
    expect(screen.queryByTestId("agent-link-cost-ada")).toBeNull();

    expect(screen.getByTestId("agent-card-tabrow-ada")).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("card-tab-chip-messages"));
    expect(onOpenTab).toHaveBeenCalledWith("ada", "Messages");
  });

  // #2464: primary-click intercept. The overlay Link and the footer
  // "Open" Link both fire `onSelect(agent.name)` instead of navigating
  // when the prop is provided so the Explorer's selection bridge can
  // dispatch in-place — the legacy `<Link>` path triggers an App Router
  // RSC navigation that pins the visible state until the transition
  // settles, eating the first click and leaving the card "highlighted
  // but not navigated".
  it("calls onSelect on primary click and prevents the Link navigation when provided", () => {
    const onSelect = vi.fn();
    render(
      <AgentCard
        agent={{
          name: "ada",
          displayName: "Ada",
          role: null,
          registeredAt: "2026-04-01T00:00:00Z",
        }}
        onSelect={onSelect}
      />,
    );

    // Overlay link → onSelect, default suppressed.
    fireEvent.click(screen.getByTestId("agent-card-link-ada"));
    expect(onSelect).toHaveBeenCalledWith("ada");

    // Footer "Open" link → same behaviour.
    onSelect.mockClear();
    fireEvent.click(screen.getByTestId("agent-open-ada"));
    expect(onSelect).toHaveBeenCalledWith("ada");
  });

  it("falls through to Link navigation when onSelect is omitted", () => {
    render(
      <AgentCard
        agent={{
          name: "ada",
          displayName: "Ada",
          role: null,
          registeredAt: "2026-04-01T00:00:00Z",
        }}
      />,
    );
    // No onClick on the Link surface means the click bubbles to the
    // anchor's default href — the dashboard/agents-list pattern.
    const link = screen.getByTestId("agent-card-link-ada");
    expect(link.getAttribute("href")).toBe("/units?node=ada&tab=Overview");
  });

  it("keeps the legacy cross-links as fallback when onOpenTab is omitted", () => {
    render(
      <AgentCard
        agent={{
          name: "ada",
          displayName: "Ada",
          role: null,
          registeredAt: "2026-04-01T00:00:00Z",
        }}
      />,
    );

    expect(
      screen.getByTestId("agent-link-conversations-ada"),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("agent-card-tabrow-ada")).toBeNull();
  });

  // PR #2390 fixed the footer row; #2441 extends the same pattern to
  // every non-overlay row. The wrapper of each row must carry
  // `pointer-events-none` so whitespace clicks fall through to the
  // full-card overlay link instead of dying on a sibling div. Interactive
  // children (parent-unit link, Edit/Delete actions, the cross-link
  // icons, the Open link) restore `pointer-events-auto` to keep their
  // own click targets.
  describe("click-gap regression (#2441)", () => {
    it("marks the parent-unit + time-ago row as pointer-events-none so whitespace clicks fall through to the overlay", () => {
      render(
        <AgentCard
          agent={{
            name: "ada",
            displayName: "Ada",
            role: null,
            registeredAt: "2026-04-01T00:00:00Z",
            parentUnit: "engineering",
          }}
        />,
      );
      const parentLink = screen.getByTestId("agent-parent-unit");
      const metaRow = parentLink.parentElement;
      expect(metaRow).not.toBeNull();
      expect(metaRow!.className).toMatch(/pointer-events-none/);
      // The parent-unit link itself stays clickable.
      expect(parentLink.className).toMatch(/pointer-events-auto/);
    });

    it("marks the lastActivity paragraph as pointer-events-none", () => {
      render(
        <AgentCard
          agent={{
            name: "ada",
            displayName: "Ada",
            role: null,
            registeredAt: "2026-04-01T00:00:00Z",
            lastActivity: "Replied to PR review",
          }}
        />,
      );
      const lastActivity = screen.getByTestId("agent-last-activity");
      expect(lastActivity.className).toMatch(/pointer-events-none/);
    });

    it("keeps the footer-strip pointer-events fix from PR #2390 intact", () => {
      render(
        <AgentCard
          agent={{
            name: "ada",
            displayName: "Ada",
            role: null,
            registeredAt: "2026-04-01T00:00:00Z",
          }}
        />,
      );
      // Footer wrapper retains `pointer-events-none`.
      const open = screen.getByTestId("agent-open-ada");
      // The Open link's nearest pointer-events-none ancestor is the
      // footer strip from PR #2390.
      const footer = open.closest(".pointer-events-none");
      expect(footer).not.toBeNull();
      // Open link itself keeps `pointer-events-auto`.
      expect(open.className).toMatch(/pointer-events-auto/);
      // Cross-link icons keep `pointer-events-auto`.
      const conv = screen.getByTestId("agent-link-conversations-ada");
      expect(conv.className).toMatch(/pointer-events-auto/);
    });
  });
});
