// Component tests for `<RuntimeStatusBadge>` (#2100). Asserts the four
// portal-rendered states (idle / busy / queued / unavailable) plus the
// loading-skeleton "unknown" path. Polling is mocked through the
// `useRuntimeStatus` hook so the test exercises only the presentation
// layer; the polling cadence is exercised in the hook's own coverage
// (manual integration via the dev server today; live-stream follow-up
// tracked separately — see PR body).

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it, vi, beforeEach } from "vitest";

import { RuntimeStatusBadge } from "./runtime-status-badge";
import type { AgentRuntimeStatusResponse } from "@/lib/api/types";

const mockUseRuntimeStatus = vi.fn();

vi.mock("@/lib/api/use-runtime-status", async () => {
  const actual = await vi.importActual<
    typeof import("@/lib/api/use-runtime-status")
  >("@/lib/api/use-runtime-status");
  return {
    ...actual,
    useRuntimeStatus: (...args: unknown[]) => mockUseRuntimeStatus(...args),
  };
});

function withClient(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

function arrange(payload: AgentRuntimeStatusResponse | undefined) {
  mockUseRuntimeStatus.mockReturnValue({
    data: payload,
    isPending: payload === undefined,
    isError: false,
  });
}

beforeEach(() => {
  mockUseRuntimeStatus.mockReset();
});

describe("RuntimeStatusBadge", () => {
  const id = "00000000-0000-0000-0000-000000000001";

  it("renders nothing when id is missing", () => {
    arrange(undefined);
    const { container } = render(
      withClient(<RuntimeStatusBadge kind="agent" id={null} />),
    );
    expect(container.firstChild).toBeNull();
  });

  it("renders the loading state on first poll", () => {
    arrange(undefined);
    render(withClient(<RuntimeStatusBadge kind="agent" id={id} />));
    const chip = screen.getByTestId("runtime-status-agent");
    expect(chip).toHaveAttribute("data-runtime-status", "unknown");
    expect(chip).toHaveTextContent(/loading/i);
  });

  it("renders the idle state with the matching label and ARIA copy", () => {
    arrange({
      status: "idle",
      lastUpdated: "2026-05-10T00:00:00Z",
      inFlightThreadCount: 0,
      queuedMessageCount: 0,
    });
    render(withClient(<RuntimeStatusBadge kind="agent" id={id} />));
    const chip = screen.getByTestId("runtime-status-agent");
    expect(chip).toHaveAttribute("data-runtime-status", "idle");
    expect(chip).toHaveTextContent("Idle");
    expect(chip).toHaveAttribute(
      "aria-label",
      expect.stringContaining("Idle"),
    );
  });

  it("renders the busy state and surfaces in-flight count in the tooltip", () => {
    arrange({
      status: "busy",
      lastUpdated: "2026-05-10T00:00:00Z",
      inFlightThreadCount: 2,
      queuedMessageCount: 0,
    });
    render(withClient(<RuntimeStatusBadge kind="agent" id={id} />));
    const chip = screen.getByTestId("runtime-status-agent");
    expect(chip).toHaveAttribute("data-runtime-status", "busy");
    expect(chip).toHaveTextContent("Busy");
    expect(chip.getAttribute("title")).toMatch(/2 thread/i);
  });

  it("renders the queued state with the queued-message count", () => {
    arrange({
      status: "queued",
      lastUpdated: "2026-05-10T00:00:00Z",
      inFlightThreadCount: 0,
      queuedMessageCount: 3,
    });
    render(withClient(<RuntimeStatusBadge kind="agent" id={id} />));
    const chip = screen.getByTestId("runtime-status-agent");
    expect(chip).toHaveAttribute("data-runtime-status", "queued");
    expect(chip).toHaveTextContent("Queued");
    expect(chip.getAttribute("title")).toMatch(/3 message/i);
  });

  it("renders the unavailable state with destructive copy", () => {
    arrange({
      status: "unavailable",
      lastUpdated: "2026-05-10T00:00:00Z",
      inFlightThreadCount: 0,
      queuedMessageCount: 0,
    });
    render(withClient(<RuntimeStatusBadge kind="agent" id={id} />));
    const chip = screen.getByTestId("runtime-status-agent");
    expect(chip).toHaveAttribute("data-runtime-status", "unavailable");
    expect(chip).toHaveTextContent("Unavailable");
  });

  it("renders the dot variant without the text label but with ARIA semantics", () => {
    arrange({
      status: "busy",
      lastUpdated: "2026-05-10T00:00:00Z",
      inFlightThreadCount: 1,
      queuedMessageCount: 0,
    });
    render(
      withClient(<RuntimeStatusBadge kind="agent" id={id} size="dot" />),
    );
    const chip = screen.getByTestId("runtime-status-agent");
    expect(chip).toHaveAttribute("data-runtime-status", "busy");
    // Dot variant has no text content but exposes ARIA + tooltip.
    expect(chip.textContent).toBe("");
    expect(chip).toHaveAttribute(
      "aria-label",
      expect.stringContaining("Busy"),
    );
  });

  it("supports the unit kind by passing it through to the polling hook", () => {
    arrange({
      status: "idle",
      lastUpdated: "2026-05-10T00:00:00Z",
      inFlightThreadCount: 0,
      queuedMessageCount: 0,
    });
    render(withClient(<RuntimeStatusBadge kind="unit" id={id} />));
    expect(mockUseRuntimeStatus).toHaveBeenCalledWith(
      "unit",
      id,
      expect.any(Object),
    );
    expect(screen.getByTestId("runtime-status-unit")).toHaveTextContent(
      "Idle",
    );
  });
});
