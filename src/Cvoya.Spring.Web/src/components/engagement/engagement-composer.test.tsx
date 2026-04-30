// Tests for the engagement composer component (E2.5 + E2.6, #1417, #1418).

import * as React from "react";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, expect, it, vi, beforeEach } from "vitest";

// ── mocks ──────────────────────────────────────────────────────────────────

const mockMutate = vi.fn();
const mockInvalidateQueries = vi.fn();

vi.mock("@tanstack/react-query", () => ({
  useMutation: (opts: {
    mutationFn: () => Promise<unknown>;
    onSuccess?: () => void;
    onError?: (err: Error) => void;
  }) => ({
    mutate: () => {
      // Invoke mutationFn and handle result.
      mockMutate(opts);
      const result = opts.mutationFn();
      if (result && typeof result.then === "function") {
        result.then(() => opts.onSuccess?.()).catch((err: Error) => opts.onError?.(err));
      }
    },
    isPending: false,
  }),
  useQueryClient: () => ({
    invalidateQueries: mockInvalidateQueries,
  }),
}));

const mockToast = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: mockToast }),
}));

// Mock api.sendThreadMessage — returns a resolved promise by default.
const mockSendThreadMessage = vi.fn().mockResolvedValue({});
vi.mock("@/lib/api/client", () => ({
  api: {
    sendThreadMessage: (...args: unknown[]) => mockSendThreadMessage(...args),
  },
}));

vi.mock("@/lib/api/query-keys", () => ({
  queryKeys: {
    threads: {
      detail: (id: string) => ["threads", "detail", id],
      all: ["threads", "all"],
      inbox: () => ["threads", "inbox"],
    },
    activity: {
      all: ["activity", "all"],
    },
  },
}));

vi.mock("@/components/thread/role", () => ({
  parseThreadSource: (address: string) => {
    const [scheme, path] = address.split("://");
    return { scheme: scheme ?? "", path: path ?? "" };
  },
}));

// ── component import ───────────────────────────────────────────────────────

import { EngagementComposer } from "./engagement-composer";

// ── tests ──────────────────────────────────────────────────────────────────

describe("EngagementComposer", () => {
  beforeEach(() => {
    mockMutate.mockClear();
    mockSendThreadMessage.mockClear().mockResolvedValue({});
    mockToast.mockClear();
    mockInvalidateQueries.mockClear();
  });

  describe("initial render", () => {
    it("renders in information mode by default", () => {
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada"]}
        />,
      );

      const form = screen.getByTestId("engagement-composer");
      expect(form).toBeInTheDocument();
      expect(form).toHaveAttribute("data-kind", "information");
      expect(form).toHaveAttribute(
        "aria-label",
        "Send message",
      );
    });

    it("renders in answer mode when initialKind=answer", () => {
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada"]}
          initialKind="answer"
        />,
      );

      const form = screen.getByTestId("engagement-composer");
      expect(form).toHaveAttribute("data-kind", "answer");
      expect(form).toHaveAttribute("aria-label", "Answer clarifying question");
    });

    it("shows the answer-mode banner when initialKind=answer", () => {
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada"]}
          initialKind="answer"
        />,
      );

      expect(screen.getByText("Answering a question")).toBeInTheDocument();
    });

    it("does NOT show the answer-mode banner in information mode", () => {
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada"]}
          initialKind="information"
        />,
      );

      expect(
        screen.queryByText("Answering a question"),
      ).not.toBeInTheDocument();
    });
  });

  describe("recipient pills", () => {
    it("shows non-human participant pills", () => {
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada", "agent://bob"]}
        />,
      );

      // Human participants are excluded from pills.
      expect(screen.queryByText("human://savas")).not.toBeInTheDocument();
      expect(screen.getByText("agent://ada")).toBeInTheDocument();
      expect(screen.getByText("agent://bob")).toBeInTheDocument();
    });

    it("clicking a participant pill updates the recipient input", () => {
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada", "agent://bob"]}
        />,
      );

      const bobPill = screen.getByText("agent://bob");
      fireEvent.click(bobPill);

      const recipientInput = screen.getByRole("textbox", {
        name: /recipient address/i,
      });
      expect(recipientInput).toHaveValue("agent://bob");
    });
  });

  describe("mode switching", () => {
    it("switches from answer to information mode when 'Send as regular message' is clicked", () => {
      // The composer is now controlled — parent owns kind. Wrap it so the
      // toggle button can flip the prop value the way the real parent does.
      function Harness() {
        const [k, setK] = React.useState<"information" | "answer">("answer");
        return (
          <EngagementComposer
            threadId="thread-abc"
            participants={["human://savas", "agent://ada"]}
            initialKind={k}
            onKindChange={setK}
          />
        );
      }

      render(<Harness />);

      expect(screen.getByTestId("engagement-composer")).toHaveAttribute(
        "data-kind",
        "answer",
      );

      fireEvent.click(
        screen.getByRole("button", { name: /switch to regular message mode/i }),
      );

      expect(screen.getByTestId("engagement-composer")).toHaveAttribute(
        "data-kind",
        "information",
      );
    });

    it("reflects initialKind prop changes (parent switches to answer mode)", () => {
      const { rerender } = render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada"]}
          initialKind="information"
        />,
      );

      expect(screen.getByTestId("engagement-composer")).toHaveAttribute(
        "data-kind",
        "information",
      );

      rerender(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada"]}
          initialKind="answer"
        />,
      );

      expect(screen.getByTestId("engagement-composer")).toHaveAttribute(
        "data-kind",
        "answer",
      );
    });
  });

  describe("submit button", () => {
    it("is disabled when the text area is empty", () => {
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada"]}
        />,
      );

      expect(screen.getByRole("button", { name: /^send$/i })).toBeDisabled();
    });

    it("is enabled when text is entered", () => {
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada"]}
        />,
      );

      fireEvent.change(
        screen.getByRole("textbox", { name: /message text/i }),
        { target: { value: "Hello" } },
      );

      expect(screen.getByRole("button", { name: /^send$/i })).not.toBeDisabled();
    });

    it("shows 'Send answer' label in answer mode", () => {
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada"]}
          initialKind="answer"
        />,
      );

      expect(
        screen.getByRole("button", { name: /send answer/i }),
      ).toBeInTheDocument();
    });
  });

  describe("successful send", () => {
    it("calls onSendSuccess after a successful send", async () => {
      const onSendSuccess = vi.fn();
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada"]}
          onSendSuccess={onSendSuccess}
        />,
      );

      fireEvent.change(
        screen.getByRole("textbox", { name: /message text/i }),
        { target: { value: "Hello" } },
      );
      fireEvent.click(screen.getByRole("button", { name: /^send$/i }));

      await waitFor(() => {
        expect(onSendSuccess).toHaveBeenCalled();
      });
    });
  });
});
