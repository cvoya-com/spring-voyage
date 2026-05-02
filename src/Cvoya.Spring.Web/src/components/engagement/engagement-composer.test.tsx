// Tests for the engagement composer component (E2.5 + E2.6, #1417, #1418).

import * as React from "react";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, expect, it, vi, beforeEach } from "vitest";

// ── mocks ──────────────────────────────────────────────────────────────────

const mockMutate = vi.fn();
const mockInvalidateQueries = vi.fn();

vi.mock("@tanstack/react-query", () => ({
  useMutation: (opts: {
    mutationFn: (vars: unknown) => Promise<unknown>;
    onSuccess?: (data: unknown, vars: unknown) => void;
    onError?: (err: Error) => void;
  }) => ({
    mutate: (vars: unknown) => {
      // Invoke mutationFn with the caller's variables and handle result.
      // The shared MessageComposer passes `{ trimmed }`; older callers pass
      // nothing — the mock supports both shapes.
      mockMutate(opts);
      const result = opts.mutationFn(vars);
      if (result && typeof result.then === "function") {
        result
          .then((data) => opts.onSuccess?.(data, vars))
          .catch((err: Error) => opts.onError?.(err));
      }
    },
    isPending: false,
  }),
  useQueryClient: () => ({
    invalidateQueries: mockInvalidateQueries,
    getQueryData: () => null,
    setQueryData: () => undefined,
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

  // #1552: the recipient is now implicit — no `To:` row, no recipient
  // input. The composer derives the default recipient from the participant
  // list and submits to it directly.
  describe("recipient (implicit)", () => {
    it("does not render a To: row or a recipient input", () => {
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada", "agent://bob"]}
        />,
      );

      expect(screen.queryByText(/^To:$/)).not.toBeInTheDocument();
      expect(
        screen.queryByRole("textbox", { name: /recipient address/i }),
      ).not.toBeInTheDocument();
    });

    it("submits to the first non-human participant", async () => {
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada", "agent://bob"]}
        />,
      );

      fireEvent.change(
        screen.getByRole("textbox", { name: /message text/i }),
        { target: { value: "Hello" } },
      );
      fireEvent.click(screen.getByRole("button", { name: /^send/i }));

      await waitFor(() => {
        expect(mockSendThreadMessage).toHaveBeenCalledWith(
          "thread-abc",
          expect.objectContaining({
            to: { scheme: "agent", path: "ada" },
            text: "Hello",
          }),
        );
      });
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

      expect(screen.getByRole("button", { name: /send message/i })).toBeDisabled();
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

      expect(
        screen.getByRole("button", { name: /send message/i }),
      ).not.toBeDisabled();
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

    // #1552: the keyboard-shortcut hint moved off the inline body text
    // onto the Send button — exposed via title attr (hover tooltip) and
    // baked into the aria-label so screen-reader users still discover it.
    it("exposes the ⌘/Ctrl+Enter shortcut on the Send button (tooltip + aria-label)", () => {
      render(
        <EngagementComposer
          threadId="thread-abc"
          participants={["human://savas", "agent://ada"]}
        />,
      );

      const button = screen.getByRole("button", { name: /send message/i });
      expect(button).toHaveAttribute("title", "⌘/Ctrl+Enter to send");
      expect(button).toHaveAttribute(
        "aria-label",
        "Send message (⌘/Ctrl+Enter)",
      );

      // The hint is no longer rendered as inline body text.
      expect(
        screen.queryByText("⌘/Ctrl+Enter to send"),
      ).not.toBeInTheDocument();
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
      fireEvent.click(screen.getByRole("button", { name: /send message/i }));

      await waitFor(() => {
        expect(onSendSuccess).toHaveBeenCalled();
      });
    });
  });
});
