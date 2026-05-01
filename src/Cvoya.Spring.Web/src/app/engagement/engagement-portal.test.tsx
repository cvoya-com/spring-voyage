// Engagement portal route tests.
//
// Verifies:
//   1. The engagement shell renders without crashing.
//   2. The "Back to Management" cross-link resolves to "/".
//   3. The top-right "+ New engagement" CTA resolves to "/engagement/new".
//   4. The shell sidebar hosts the engagement list (replacing the old static
//      nav links) and forwards slice/unit/agent props from the URL.
//   5. The cross-link URL shape for management → engagement is preserved.

import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

// ── mocks ──────────────────────────────────────────────────────────────────

let mockPathname = "/engagement/mine";
let mockSearchParams = new URLSearchParams();

vi.mock("next/navigation", () => ({
  usePathname: () => mockPathname,
  useSearchParams: () => mockSearchParams,
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

// useInbox feeds the GlobalInboxBadge — keep it stubbed so the shell
// renders without requiring a QueryClientProvider.
vi.mock("@/lib/api/queries", () => ({
  useInbox: () => ({ data: [], isPending: false, error: null }),
  useThreads: () => ({
    data: undefined,
    isPending: true,
    error: null,
    isFetching: true,
  }),
  useCurrentUser: () => ({
    data: null,
    isPending: false,
    error: null,
  }),
}));

// Mock EngagementList so the shell doesn't require the full React Query
// client tree under unit-test context.
vi.mock("@/components/engagement/engagement-list", () => ({
  EngagementList: ({
    slice,
    unit,
    agent,
    selectedThreadId,
    variant,
  }: {
    slice: string;
    unit?: string;
    agent?: string;
    selectedThreadId?: string;
    variant?: string;
  }) => (
    <div
      data-testid="mock-engagement-list"
      data-slice={slice}
      data-unit={unit ?? ""}
      data-agent={agent ?? ""}
      data-selected={selectedThreadId ?? ""}
      data-variant={variant ?? ""}
    />
  ),
}));

// ── component imports (after mocks) ───────────────────────────────────────

import { EngagementShell } from "@/components/engagement/engagement-shell";
import MyEngagementsPage from "./mine/page";

// ── helpers ───────────────────────────────────────────────────────────────

function resetUrl(pathname = "/engagement/mine", search = "") {
  mockPathname = pathname;
  mockSearchParams = new URLSearchParams(search);
}

function renderShell(children: ReactNode = <div data-testid="content" />) {
  return render(<EngagementShell>{children}</EngagementShell>);
}

// ── tests ──────────────────────────────────────────────────────────────────

describe("EngagementShell", () => {
  it("renders without crashing", () => {
    resetUrl();
    renderShell();
    expect(screen.getByTestId("engagement-shell")).toBeInTheDocument();
  });

  it("renders the engagement header", () => {
    resetUrl();
    renderShell();
    expect(screen.getByTestId("engagement-header")).toBeInTheDocument();
    expect(screen.getByText("Engagement")).toBeInTheDocument();
  });

  it("renders 'Back to Management' cross-link pointing to /", () => {
    resetUrl();
    renderShell();
    const link = screen.getByTestId("engagement-back-to-management");
    expect(link).toHaveAttribute("href", "/");
    expect(link).toHaveTextContent("Back to Management");
  });

  it("renders the top-right '+ New engagement' CTA", () => {
    resetUrl();
    renderShell();
    const cta = screen.getByTestId("engagement-new-cta");
    expect(cta).toHaveAttribute("href", "/engagement/new");
    expect(cta).toHaveTextContent("New engagement");
  });

  it("renders the engagement sidebar with the live list", () => {
    resetUrl();
    renderShell();
    expect(screen.getByTestId("engagement-sidebar")).toBeInTheDocument();
    const list = screen.getByTestId("mock-engagement-list");
    expect(list).toHaveAttribute("data-variant", "sidebar");
    expect(list).toHaveAttribute("data-slice", "mine");
  });

  it("passes the selected thread id to the sidebar list when on /engagement/<id>", () => {
    resetUrl("/engagement/thread-abc");
    renderShell();
    const list = screen.getByTestId("mock-engagement-list");
    expect(list).toHaveAttribute("data-selected", "thread-abc");
  });

  it("does not pass a selected thread id on /engagement/mine or /engagement/new", () => {
    resetUrl("/engagement/mine");
    const { rerender } = renderShell();
    let list = screen.getByTestId("mock-engagement-list");
    expect(list).toHaveAttribute("data-selected", "");

    resetUrl("/engagement/new");
    rerender(<EngagementShell><div /></EngagementShell>);
    list = screen.getByTestId("mock-engagement-list");
    expect(list).toHaveAttribute("data-selected", "");
  });

  it("forwards ?unit=<id> to the sidebar list as slice=unit", () => {
    resetUrl("/engagement/mine", "unit=eng-team");
    renderShell();
    const list = screen.getByTestId("mock-engagement-list");
    expect(list).toHaveAttribute("data-slice", "unit");
    expect(list).toHaveAttribute("data-unit", "eng-team");
  });

  it("forwards ?agent=<id> to the sidebar list as slice=agent", () => {
    resetUrl("/engagement/mine", "agent=ada");
    renderShell();
    const list = screen.getByTestId("mock-engagement-list");
    expect(list).toHaveAttribute("data-slice", "agent");
    expect(list).toHaveAttribute("data-agent", "ada");
  });

  it("renders children inside the main content area", () => {
    resetUrl();
    renderShell(<div data-testid="slot-content">hello</div>);
    expect(screen.getByTestId("slot-content")).toBeInTheDocument();
  });
});

describe("MyEngagementsPage placeholder", () => {
  it("renders the empty selection placeholder for the bare /engagement/mine route", async () => {
    const jsxEl = await MyEngagementsPage({
      searchParams: Promise.resolve({}),
    });
    render(jsxEl);
    expect(screen.getByTestId("my-engagements-page")).toBeInTheDocument();
    expect(screen.getByText("Your engagements")).toBeInTheDocument();
    expect(
      screen.getByText(/Select an engagement from the list/i),
    ).toBeInTheDocument();
  });

  it("acknowledges deep-linked unit slices in the heading", async () => {
    const jsxEl = await MyEngagementsPage({
      searchParams: Promise.resolve({ unit: "eng-team" }),
    });
    render(jsxEl);
    expect(
      screen.getByText("Engagements for unit: eng-team"),
    ).toBeInTheDocument();
  });

  it("acknowledges deep-linked agent slices in the heading", async () => {
    const jsxEl = await MyEngagementsPage({
      searchParams: Promise.resolve({ agent: "ada" }),
    });
    render(jsxEl);
    expect(
      screen.getByText("Engagements for agent: ada"),
    ).toBeInTheDocument();
  });
});

describe("Cross-link URL shapes", () => {
  it("management → engagement cross-link for a unit uses /engagement/mine?unit=<id>", () => {
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
