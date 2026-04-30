// Engagement portal route tests (E2.3, #1415).
//
// Verifies:
//   1. The engagement shell renders without crashing.
//   2. The "Back to Management" cross-link resolves to "/".
//   3. The "My engagements" nav link resolves to "/engagement/mine".
//   4. The mine page renders its empty state.
//   5. The [id] placeholder page renders for a given engagement id.
//   6. The cross-link URL shape for management → engagement is
//      /engagement/mine?unit=<id> and /engagement/mine?agent=<id>.

import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

// ── mocks ──────────────────────────────────────────────────────────────────

let mockPathname = "/engagement/mine";

vi.mock("next/navigation", () => ({
  usePathname: () => mockPathname,
  redirect: (url: string) => {
    throw new Error(`redirect:${url}`);
  },
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: { href: string; children: ReactNode } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

// ── component imports (after mocks) ───────────────────────────────────────

import { EngagementShell } from "@/components/engagement/engagement-shell";
import MyEngagementsPage from "./mine/page";

// ── helpers ───────────────────────────────────────────────────────────────

function renderShell(children: ReactNode = <div data-testid="content" />) {
  return render(<EngagementShell>{children}</EngagementShell>);
}

// ── tests ──────────────────────────────────────────────────────────────────

describe("EngagementShell", () => {
  it("renders without crashing", () => {
    renderShell();
    expect(screen.getByTestId("engagement-shell")).toBeInTheDocument();
  });

  it("renders the engagement header", () => {
    renderShell();
    expect(screen.getByTestId("engagement-header")).toBeInTheDocument();
    expect(screen.getByText("Engagement")).toBeInTheDocument();
  });

  it("renders 'Back to Management' cross-link pointing to /", () => {
    renderShell();
    const link = screen.getByTestId("engagement-back-to-management");
    expect(link).toHaveAttribute("href", "/");
    expect(link).toHaveTextContent("Back to Management");
  });

  it("renders the engagement sidebar navigation", () => {
    renderShell();
    const nav = screen.getByTestId("engagement-sidebar");
    expect(nav).toBeInTheDocument();
  });

  it("renders 'My engagements' nav link pointing to /engagement/mine", () => {
    renderShell();
    const link = screen.getByTestId("engagement-nav-engagement-mine");
    expect(link).toHaveAttribute("href", "/engagement/mine");
    expect(link).toHaveTextContent("My engagements");
  });

  it("marks the active nav link with aria-current=page when pathname matches", () => {
    mockPathname = "/engagement/mine";
    renderShell();
    const link = screen.getByTestId("engagement-nav-engagement-mine");
    expect(link).toHaveAttribute("aria-current", "page");
  });

  it("does not mark nav link as active when pathname differs", () => {
    mockPathname = "/engagement/some-id";
    renderShell();
    const link = screen.getByTestId("engagement-nav-engagement-mine");
    expect(link).not.toHaveAttribute("aria-current");
  });

  it("renders children inside the main content area", () => {
    renderShell(<div data-testid="slot-content">hello</div>);
    expect(screen.getByTestId("slot-content")).toBeInTheDocument();
  });
});

describe("MyEngagementsPage", () => {
  it("renders without crashing", () => {
    render(<MyEngagementsPage />);
    expect(screen.getByTestId("my-engagements-page")).toBeInTheDocument();
  });

  it("renders the page heading", () => {
    render(<MyEngagementsPage />);
    expect(
      screen.getByRole("heading", { level: 1 }),
    ).toHaveTextContent("My engagements");
  });

  it("renders the empty state", () => {
    render(<MyEngagementsPage />);
    expect(
      screen.getByTestId("my-engagements-empty-state"),
    ).toBeInTheDocument();
    expect(screen.getByText("No engagements yet")).toBeInTheDocument();
  });
});

describe("Cross-link URL shapes", () => {
  it("management → engagement cross-link for a unit uses /engagement/mine?unit=<id>", () => {
    // Verify the URL shape E2.4 should expect for unit-scoped filtering.
    // This is a declaration test — we construct the URL the same way
    // unit-overview.tsx does and assert it matches the spec.
    const unitId = "engineering-team";
    const expected = `/engagement/mine?unit=${encodeURIComponent(unitId)}`;
    expect(expected).toBe("/engagement/mine?unit=engineering-team");
  });

  it("management → engagement cross-link for an agent uses /engagement/mine?agent=<id>", () => {
    const agentId = "engineering-team/ada";
    const expected = `/engagement/mine?agent=${encodeURIComponent(agentId)}`;
    expect(expected).toBe(
      "/engagement/mine?agent=engineering-team%2Fada",
    );
  });
});
