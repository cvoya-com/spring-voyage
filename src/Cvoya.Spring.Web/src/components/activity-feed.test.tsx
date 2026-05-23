import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ActivityFeed } from "./activity-feed";
import type { ActivityEvent } from "@/lib/api/types";

const mockEvent: ActivityEvent = {
  id: "abc-123",
  timestamp: new Date().toISOString(),
  source: { scheme: "agent", path: "test-agent" },
  eventType: "MessageArrived",
  severity: "Info",
  summary: "Test event happened",
};

describe("ActivityFeed", () => {
  it("shows empty message when no items", () => {
    render(<ActivityFeed items={[]} />);
    expect(screen.getByText("No activity yet")).toBeInTheDocument();
  });

  it("renders activity events", () => {
    render(<ActivityFeed items={[mockEvent]} />);
    expect(screen.getByText("Test event happened")).toBeInTheDocument();
    expect(screen.getByText(/agent:\/\/test-agent/)).toBeInTheDocument();
  });

  it("shows the Activity Feed heading", () => {
    render(<ActivityFeed items={[mockEvent]} />);
    expect(screen.getByText("Activity Feed")).toBeInTheDocument();
  });
});
