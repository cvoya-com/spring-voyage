// Tests for `ThreadEventCard` and the `shouldRenderAsCard` heuristic
// (#1630). The card is the renderer for non-message events in a thread
// timeline; the compact state shows a friendly label / summary, and the
// expand panel surfaces technical details (raw IDs, addresses, severity)
// for diagnostic use.

import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { ThreadEvent } from "@/lib/api/types";

import {
  shouldRenderAsCard,
  ThreadEventCard,
} from "./thread-event-card";

const ADA_ID = "a1b2c3d4-0000-0000-0000-000000000001";

function makeEvent(overrides: Partial<ThreadEvent> = {}): ThreadEvent {
  return {
    id: "00000000-0000-0000-0000-000000000001",
    timestamp: "2026-04-26T12:00:00Z",
    source: { address: `agent:id:${ADA_ID}`, displayName: "ada" },
    eventType: "StateChanged",
    severity: "Info",
    summary: "agent finished its current step",
    ...overrides,
  };
}

describe("ThreadEventCard", () => {
  it("renders a friendly label for known event types", () => {
    render(<ThreadEventCard event={makeEvent()} />);
    expect(screen.getByText("State changed")).toBeInTheDocument();
  });

  it("renders the source displayName next to the timestamp", () => {
    render(<ThreadEventCard event={makeEvent()} />);
    expect(
      screen.getByTestId("conversation-event-card-source-name"),
    ).toHaveTextContent("ada");
  });

  it("expands to reveal the raw envelope details on click", () => {
    const id = "d4ce4258-ab40-4c10-be06-407cc5ec9139";
    render(
      <ThreadEventCard
        event={makeEvent({
          id,
          source: { address: `agent:id:${ADA_ID}`, displayName: "ada" },
          summary: "agent finished its current step",
        })}
      />,
    );
    const toggle = screen.getByTestId(
      `conversation-event-card-${id}-toggle`,
    );
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    fireEvent.click(toggle);
    expect(toggle).toHaveAttribute("aria-expanded", "true");

    const details = screen.getByTestId(
      `conversation-event-card-${id}-details`,
    );
    expect(details).toBeInTheDocument();
    // Technical IDs only surface inside the expand panel.
    expect(details).toHaveTextContent(id);
    expect(details).toHaveTextContent(`agent:id:${ADA_ID}`);
    expect(details).toHaveTextContent("StateChanged");
  });

  it("uses event.from for source attribution when present", () => {
    render(
      <ThreadEventCard
        event={makeEvent({
          // Receiver-projected: the human emitted the event.
          source: { address: "human://savas", displayName: "savas" },
          // Underlying sender: the agent.
          from: { address: `agent:id:${ADA_ID}`, displayName: "ada" },
        })}
      />,
    );
    expect(
      screen.getByTestId("conversation-event-card-source-name"),
    ).toHaveTextContent("ada");
  });

  it("prefers event.body over event.summary when both are present", () => {
    render(
      <ThreadEventCard
        event={makeEvent({
          body: "Hello savas!",
          summary: "engine summary line",
        })}
      />,
    );
    expect(screen.getByText("Hello savas!")).toBeInTheDocument();
    expect(screen.queryByText("engine summary line")).toBeNull();
  });

  it("falls back to event.summary when body is absent", () => {
    render(
      <ThreadEventCard
        event={makeEvent({
          body: undefined,
          summary: "agent finished its current step",
        })}
      />,
    );
    const summary = screen.getByTestId("conversation-event-card-summary");
    expect(summary).toHaveTextContent("agent finished its current step");
  });

  it("falls back to the friendly event-type label when both body and summary are absent", () => {
    render(
      <ThreadEventCard
        event={makeEvent({
          body: undefined,
          summary: undefined,
        })}
      />,
    );
    const summary = screen.getByTestId("conversation-event-card-summary");
    expect(summary).toHaveTextContent("State changed");
  });

  it("escalates tone to destructive for severity=Error events", () => {
    render(
      <ThreadEventCard
        event={makeEvent({ severity: "Error", summary: "boom" })}
      />,
    );
    const card = screen.getByTestId(
      "conversation-event-card-00000000-0000-0000-0000-000000000001",
    );
    expect(card.className).toMatch(/destructive/);
  });
});

describe("shouldRenderAsCard", () => {
  it("treats lifecycle events as cards", () => {
    expect(shouldRenderAsCard(makeEvent({ eventType: "StateChanged" }))).toBe(
      true,
    );
    expect(
      shouldRenderAsCard(
        makeEvent({ eventType: "WorkflowStepCompleted" }),
      ),
    ).toBe(true);
  });

  it("treats tool calls as cards", () => {
    expect(shouldRenderAsCard(makeEvent({ eventType: "DecisionMade" }))).toBe(
      true,
    );
  });

  it("treats MessageReceived as a bubble (never a card)", () => {
    expect(
      shouldRenderAsCard(
        makeEvent({ eventType: "MessageReceived", body: "hi" }),
      ),
    ).toBe(false);
    // Body-less message events still take the bubble path — the
    // platform now guarantees a usable summary upstream (#1641).
    expect(
      shouldRenderAsCard(makeEvent({ eventType: "MessageReceived" })),
    ).toBe(false);
  });

  it("treats MessageSent as a bubble (never a card)", () => {
    expect(
      shouldRenderAsCard(
        makeEvent({ eventType: "MessageSent", body: "hi" }),
      ),
    ).toBe(false);
  });
});
