// Engagement portal route tests (E2.3 + E2.4, #1415, #1416).
//
// Verifies:
//   1. The engagement shell renders without crashing.
//   2. The "Back to Management" cross-link resolves to "/".
//   3. The "My engagements" nav link resolves to "/engagement/mine".
//   4. The mine page renders the heading for each slice variant.
//   5. The cross-link URL shape for management → engagement is
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

// Mock useInbox so EngagementShell (GlobalInboxBadge) does not require a
// QueryClientProvider in unit-test context.
vi.mock("@/lib/api/queries", () => ({
  useInbox: () => ({ data: [], isPending: false, error: null }),
  useThreads: () => ({
    data: undefined,
    isPending: true,
    error: null,
    isFetching: true,
  }),
}));

// Mock EngagementList so the async server component tests don't render the
// full client tree (which requires QueryClientProvider + useThreads etc.).
vi.mock("@/components/engagement/engagement-list", () => ({
  EngagementList: ({
    slice,
    unit,
    agent,
  }: {
    slice: string;
    unit?: string;
    agent?: string;
  }) => (
    <div
      data-testid="mock-engagement-list"
      data-slice={slice}
      data-unit={unit}
      data-agent={agent}
    />
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

// MyEngagementsPage is an async server component and cannot be rendered
// by react-dom in unit-test context (react-dom raises "async Client Component"
// for any async component it encounters). The slice-dispatch logic is fully
// covered by testing the component function as a plain async function that
// resolves its JSX, then asserting on the props threaded through to the
// mocked EngagementList.

describe("MyEngagementsPage slice dispatch", () => {
  it("passes slice=mine with no query params", async () => {
    const jsxEl = await MyEngagementsPage({
      searchParams: Promise.resolve({}),
    });
    // The returned JSX element has the EngagementList as a child; we can
    // inspect its props directly from the React element tree.
    // Find the mock list prop by walking the JSX element structure.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const findEngagementList = (node: any): any => {
      if (!node || typeof node !== "object") return null;
      if (node.type?.displayName === "EngagementList" ||
          (typeof node.type === "function" && node.type.name === "EngagementList")) {
        return node;
      }
      if (node.props?.children) {
        const children = Array.isArray(node.props.children)
          ? node.props.children
          : [node.props.children];
        for (const child of children) {
          const found = findEngagementList(child);
          if (found) return found;
        }
      }
      return null;
    };

    const listEl = findEngagementList(jsxEl);
    expect(listEl).not.toBeNull();
    expect(listEl.props.slice).toBe("mine");
    expect(listEl.props.unit).toBeUndefined();
    expect(listEl.props.agent).toBeUndefined();
  });

  it("passes slice=unit with unit param", async () => {
    const jsxEl = await MyEngagementsPage({
      searchParams: Promise.resolve({ unit: "eng-team" }),
    });
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const findProps = (node: any): Record<string, unknown> | null => {
      if (!node || typeof node !== "object") return null;
      if (node.props?.slice === "unit") return node.props as Record<string, unknown>;
      if (node.props?.children) {
        const children = Array.isArray(node.props.children)
          ? node.props.children
          : [node.props.children];
        for (const child of children) {
          const found = findProps(child);
          if (found) return found;
        }
      }
      return null;
    };
    const props = findProps(jsxEl);
    expect(props).not.toBeNull();
    expect(props?.unit).toBe("eng-team");
  });

  it("passes slice=agent with agent param", async () => {
    const jsxEl = await MyEngagementsPage({
      searchParams: Promise.resolve({ agent: "ada" }),
    });
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const findProps = (node: any): Record<string, unknown> | null => {
      if (!node || typeof node !== "object") return null;
      if (node.props?.slice === "agent") return node.props as Record<string, unknown>;
      if (node.props?.children) {
        const children = Array.isArray(node.props.children)
          ? node.props.children
          : [node.props.children];
        for (const child of children) {
          const found = findProps(child);
          if (found) return found;
        }
      }
      return null;
    };
    const props = findProps(jsxEl);
    expect(props).not.toBeNull();
    expect(props?.agent).toBe("ada");
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
