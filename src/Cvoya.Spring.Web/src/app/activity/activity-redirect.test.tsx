import { describe, expect, it, vi } from "vitest";

// `next/navigation`'s `redirect()` throws an internal Next.js signal — we
// mock it here so the assertion is a clean "called with /activity/events"
// without trapping a control-flow exception.
const redirectMock = vi.fn();
vi.mock("next/navigation", () => ({
  redirect: (...args: unknown[]) => redirectMock(...args),
}));

import ActivityIndexRedirect from "./page";

describe("ActivityIndexRedirect", () => {
  it("redirects /activity to /activity/events", () => {
    ActivityIndexRedirect();
    expect(redirectMock).toHaveBeenCalledWith("/activity/events");
  });
});
