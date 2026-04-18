import { render, screen } from "@testing-library/react";
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
      "/agents/ada",
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
          status: "active",
          executionMode: "Auto",
        }}
      />,
    );

    expect(screen.getByTestId("agent-parent-unit")).toHaveAttribute(
      "href",
      "/units/engineering",
    );
    expect(
      screen.getByTestId("agent-link-conversations-engineering/ada"),
    ).toHaveAttribute("href", "/agents/engineering%2Fada?tab=conversations");
    expect(
      screen.getByTestId("agent-link-cost-engineering/ada"),
    ).toHaveAttribute("href", "/agents/engineering%2Fada?tab=costs");
    expect(screen.getByTestId("agent-execution-mode-badge")).toHaveTextContent(
      "Auto",
    );
    expect(screen.getByTestId("agent-status-badge")).toHaveTextContent(
      "active",
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
    expect(link).toHaveAttribute("href", "/agents/ada");
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
});
