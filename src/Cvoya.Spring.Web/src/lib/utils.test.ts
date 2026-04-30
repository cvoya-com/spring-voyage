import { describe, expect, it } from "vitest";
import { cn, formatCost, humanEventType, timeAgo } from "./utils";

describe("cn", () => {
  it("merges class names", () => {
    expect(cn("foo", "bar")).toBe("foo bar");
  });

  it("handles conditional classes", () => {
    expect(cn("base", false && "hidden", "visible")).toBe("base visible");
  });

  it("deduplicates tailwind classes", () => {
    expect(cn("p-4", "p-2")).toBe("p-2");
  });
});

describe("timeAgo", () => {
  it("returns seconds ago for recent timestamps", () => {
    const now = new Date(Date.now() - 30_000).toISOString();
    expect(timeAgo(now)).toBe("30s ago");
  });

  it("returns minutes ago", () => {
    const fiveMinAgo = new Date(Date.now() - 300_000).toISOString();
    expect(timeAgo(fiveMinAgo)).toBe("5m ago");
  });

  it("returns hours ago", () => {
    const twoHoursAgo = new Date(Date.now() - 7_200_000).toISOString();
    expect(timeAgo(twoHoursAgo)).toBe("2h ago");
  });

  it("returns days ago", () => {
    const threeDaysAgo = new Date(Date.now() - 259_200_000).toISOString();
    expect(timeAgo(threeDaysAgo)).toBe("3d ago");
  });
});

describe("formatCost", () => {
  it("formats USD amounts", () => {
    expect(formatCost(12.5)).toBe("$12.50");
    expect(formatCost(0)).toBe("$0.00");
    expect(formatCost(1234.567)).toBe("$1234.57");
  });
});

// #989: humanEventType maps raw identifiers to short user-friendly labels
// so operators see "Message received" instead of "MessageReceived" in the
// activity feed, dashboard, and unit/agent Activity tabs.
describe("humanEventType (#989)", () => {
  it("maps every known ActivityEventType to a readable label", () => {
    expect(humanEventType("MessageReceived")).toBe("Message received");
    expect(humanEventType("MessageSent")).toBe("Message sent");
    expect(humanEventType("ThreadStarted")).toBe("Thread started");
    expect(humanEventType("ThreadCompleted")).toBe("Thread completed");
    expect(humanEventType("DecisionMade")).toBe("Decision made");
    expect(humanEventType("ErrorOccurred")).toBe("Error");
    expect(humanEventType("StateChanged")).toBe("State changed");
    expect(humanEventType("InitiativeTriggered")).toBe("Initiative triggered");
    expect(humanEventType("ReflectionCompleted")).toBe("Reflection completed");
    expect(humanEventType("WorkflowStepCompleted")).toBe("Workflow step completed");
    expect(humanEventType("CostIncurred")).toBe("Cost incurred");
    expect(humanEventType("TokenDelta")).toBe("Tokens used");
    expect(humanEventType("ValidationProgress")).toBe("Validation progress");
  });

  it("falls back to the raw identifier for unknown event types", () => {
    // A new server-side event type added before the client is updated
    // should degrade gracefully rather than silently disappearing.
    expect(humanEventType("SomeNewEventType")).toBe("SomeNewEventType");
    expect(humanEventType("")).toBe("");
  });
});
