// Tests for `ThreadEventCard` and the `shouldRenderAsCard` heuristic
// (#1630). The card is the observer-view fallback for non-message
// events; it must never leak raw GUIDs in its compact-state copy and
// must reveal the technical details only when the user clicks expand.

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

  it("does NOT leak a raw GUID in the compact state", () => {
    const id = "d4ce4258-ab40-4c10-be06-407cc5ec9139";
    render(
      <ThreadEventCard
        event={makeEvent({
          id,
          summary: `Received Domain message ${id} from human:id:${id}`,
        })}
      />,
    );
    // Card visible state must show the friendly label, not the GUID.
    const card = screen.getByTestId(`conversation-event-card-${id}`);
    expect(card).toBeInTheDocument();
    expect(card).not.toHaveTextContent(id);
    // Friendly label takes over because the summary matched the
    // "Received Domain message …" envelope template.
    const summary = screen.getByTestId("conversation-event-card-summary");
    expect(summary).toHaveTextContent("State changed");
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

  it("shows the message body verbatim when present", () => {
    render(
      <ThreadEventCard
        event={makeEvent({
          eventType: "MessageReceived",
          body: "Hello savas!",
          summary: "envelope summary",
        })}
      />,
    );
    expect(screen.getByText("Hello savas!")).toBeInTheDocument();
    expect(screen.queryByText("envelope summary")).toBeNull();
  });

  it("strips the 'Received Domain message <uuid> …' envelope template", () => {
    const id = "d4ce4258-ab40-4c10-be06-407cc5ec9139";
    render(
      <ThreadEventCard
        event={makeEvent({
          eventType: "MessageReceived",
          summary: `Received Domain message ${id} from human:id:${id}`,
        })}
      />,
    );
    const summary = screen.getByTestId("conversation-event-card-summary");
    // Falls back to the friendly label rather than leaking the GUID.
    expect(summary).toHaveTextContent("Message");
    expect(summary).not.toHaveTextContent(id);
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

  it("treats MessageReceived with a body as a bubble (not a card)", () => {
    expect(
      shouldRenderAsCard(
        makeEvent({ eventType: "MessageReceived", body: "hi" }),
      ),
    ).toBe(false);
  });

  it("treats body-less MessageReceived as a card (#1630 envelope-leak case)", () => {
    expect(
      shouldRenderAsCard(makeEvent({ eventType: "MessageReceived" })),
    ).toBe(true);
  });
});
