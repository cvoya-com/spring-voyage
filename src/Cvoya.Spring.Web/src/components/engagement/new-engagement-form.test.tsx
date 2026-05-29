// Unit tests for the new-engagement form (#1455 / #1456).

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { NewEngagementForm } from "./new-engagement-form";
import type { ValidatedTenantTreeNode } from "@/lib/api/validate-tenant-tree";

// ── next/navigation stubs ────────────────────────────────────────────────
const pushMock = vi.fn();
let currentSearchParams = new URLSearchParams();

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: pushMock }),
  useSearchParams: () => currentSearchParams,
}));

vi.mock("next/link", () => ({
  default: ({ href, children }: { href: string; children: ReactNode }) => (
    <a href={href}>{children}</a>
  ),
}));

// ── data hooks ───────────────────────────────────────────────────────────
const useTenantTreeMock = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useTenantTree: () => useTenantTreeMock(),
}));

// ── api client ───────────────────────────────────────────────────────────
const sendMessageMock = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    sendMessage: (body: unknown) => sendMessageMock(body),
  },
  ApiError: class extends Error {},
}));

// ── toast ─────────────────────────────────────────────────────────────────
const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

// ── render harness ────────────────────────────────────────────────────────
function renderForm() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <NewEngagementForm />
    </QueryClientProvider>,
  );
}

// ── tree fixture ─────────────────────────────────────────────────────────
const tree: ValidatedTenantTreeNode = {
  id: "tenant://default",
  name: "default",
  kind: "Tenant",
  status: "running",
  children: [
    {
      id: "engineering",
      name: "Engineering",
      kind: "Unit",
      status: "stopped",
      children: [
        {
          id: "ada",
          name: "Ada Lovelace",
          kind: "Agent",
          status: "running",
          primaryParentId: "engineering",
        },
      ],
    },
    {
      id: "design",
      name: "Design",
      kind: "Unit",
      status: "stopped",
      children: [],
    },
  ],
};

beforeEach(() => {
  pushMock.mockClear();
  sendMessageMock.mockReset();
  toastMock.mockClear();
  currentSearchParams = new URLSearchParams();
  useTenantTreeMock.mockReset();
  useTenantTreeMock.mockReturnValue({
    data: tree,
    isPending: false,
    isError: false,
    error: null,
  });
});

describe("NewEngagementForm — picker", () => {
  it("renders every Unit and Agent in the tenant tree", () => {
    renderForm();
    expect(
      screen.getByTestId("engagement-new-pick-unit-engineering"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("engagement-new-pick-unit-design"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("engagement-new-pick-agent-ada"),
    ).toBeInTheDocument();
  });

  it("filters by name and address", () => {
    renderForm();
    fireEvent.change(screen.getByTestId("engagement-new-filter"), {
      target: { value: "ada" },
    });
    expect(
      screen.getByTestId("engagement-new-pick-agent-ada"),
    ).toBeInTheDocument();
    expect(
      screen.queryByTestId("engagement-new-pick-unit-engineering"),
    ).toBeNull();
  });

  it("toggles a pick and shows it as a chip", () => {
    renderForm();
    fireEvent.click(screen.getByTestId("engagement-new-pick-unit-engineering"));
    expect(
      screen.getByTestId("engagement-new-chip-unit-engineering"),
    ).toBeInTheDocument();
    // Click again to remove.
    fireEvent.click(screen.getByTestId("engagement-new-pick-unit-engineering"));
    expect(
      screen.queryByTestId("engagement-new-chip-unit-engineering"),
    ).toBeNull();
  });
});

describe("NewEngagementForm — pre-population (#1456)", () => {
  it("seeds participants from `?participant=` query strings", () => {
    currentSearchParams = new URLSearchParams();
    currentSearchParams.append("participant", "unit://engineering");
    currentSearchParams.append("participant", "agent://ada");
    renderForm();
    expect(
      screen.getByTestId("engagement-new-chip-unit-engineering"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("engagement-new-chip-agent-ada"),
    ).toBeInTheDocument();
  });

  it("ignores malformed `?participant=` values", () => {
    currentSearchParams = new URLSearchParams();
    currentSearchParams.append("participant", "garbage");
    currentSearchParams.append("participant", "unit://engineering");
    renderForm();
    expect(
      screen.getByTestId("engagement-new-chip-unit-engineering"),
    ).toBeInTheDocument();
    expect(
      screen.queryByTestId("engagement-new-selected"),
    ).toBeInTheDocument();
  });

  it("a seeded participant is removable before submit", () => {
    currentSearchParams = new URLSearchParams();
    currentSearchParams.append("participant", "unit://engineering");
    renderForm();
    fireEvent.click(
      screen.getByTestId("engagement-new-chip-remove-unit-engineering"),
    );
    expect(
      screen.queryByTestId("engagement-new-chip-unit-engineering"),
    ).toBeNull();
  });
});

describe("NewEngagementForm — submit", () => {
  it("blocks submit with an inline error when no participants are picked", async () => {
    renderForm();
    fireEvent.change(screen.getByTestId("engagement-new-body"), {
      target: { value: "hello" },
    });
    fireEvent.click(screen.getByTestId("engagement-new-submit"));
    const error = await screen.findByTestId("engagement-new-error");
    expect(error).toHaveTextContent(/at least one participant/i);
    expect(sendMessageMock).not.toHaveBeenCalled();
  });

  it("blocks submit with an inline error when the body is empty", async () => {
    renderForm();
    fireEvent.click(screen.getByTestId("engagement-new-pick-unit-engineering"));
    fireEvent.click(screen.getByTestId("engagement-new-submit"));
    const error = await screen.findByTestId("engagement-new-error");
    expect(error).toHaveTextContent(/first message/i);
    expect(sendMessageMock).not.toHaveBeenCalled();
  });

  it("sends one message with the picked participant in recipients[] and navigates", async () => {
    sendMessageMock.mockResolvedValueOnce({
      threadId: "thread-1",
      messageId: "msg-1",
    });
    renderForm();
    fireEvent.click(screen.getByTestId("engagement-new-pick-unit-engineering"));
    fireEvent.change(screen.getByTestId("engagement-new-body"), {
      target: { value: "Kick off the work." },
    });
    fireEvent.click(screen.getByTestId("engagement-new-submit"));

    await waitFor(() => {
      expect(sendMessageMock).toHaveBeenCalledTimes(1);
    });
    expect(sendMessageMock).toHaveBeenCalledWith({
      to: null,
      type: "Domain",
      threadId: null,
      payload: "Kick off the work.",
      recipients: [{ scheme: "unit", path: "engineering" }],
    });
    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith("/engagement/thread-1");
    });
  });

  it("sends ONE message carrying every picked participant in recipients[] — no per-recipient loop (#2887)", async () => {
    // #2887 / #2890 — the form must POST a single send with the full recipient
    // set; the server resolves one shared thread from {sender} ∪ recipients
    // and fans out. The previous client looped one POST per recipient, and the
    // old test forced both calls to echo the same stubbed threadId — so it
    // proved the loop, never a real shared thread. Asserting a SINGLE call
    // carrying all recipients is the real client contract: a regression back
    // to looping (or dropping a recipient) fails this test.
    sendMessageMock.mockResolvedValueOnce({
      threadId: "thread-7",
      messageId: "m-1",
    });
    renderForm();
    fireEvent.click(screen.getByTestId("engagement-new-pick-unit-engineering"));
    fireEvent.click(screen.getByTestId("engagement-new-pick-agent-ada"));
    fireEvent.change(screen.getByTestId("engagement-new-body"), {
      target: { value: "Multi-party hello." },
    });
    fireEvent.click(screen.getByTestId("engagement-new-submit"));

    await waitFor(() => {
      expect(sendMessageMock).toHaveBeenCalledTimes(1);
    });
    expect(sendMessageMock).toHaveBeenCalledWith({
      to: null,
      type: "Domain",
      threadId: null,
      payload: "Multi-party hello.",
      recipients: [
        { scheme: "unit", path: "engineering" },
        { scheme: "agent", path: "ada" },
      ],
    });
    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith("/engagement/thread-7");
    });
  });
});
