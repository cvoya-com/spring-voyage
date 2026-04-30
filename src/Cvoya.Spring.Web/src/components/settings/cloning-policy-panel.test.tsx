import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

const useTenantCloningPolicyMock = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useTenantCloningPolicy: () => useTenantCloningPolicyMock(),
}));

import { CloningPolicyPanel } from "./cloning-policy-panel";

describe("CloningPolicyPanel (#534)", () => {
  it("renders loading skeleton while fetching", () => {
    useTenantCloningPolicyMock.mockReturnValueOnce({
      data: undefined,
      isPending: true,
      isError: false,
    });
    render(<CloningPolicyPanel />);
    expect(
      screen.getByTestId("settings-cloning-policy-loading"),
    ).toBeInTheDocument();
  });

  it("renders error state when fetch fails", () => {
    useTenantCloningPolicyMock.mockReturnValueOnce({
      data: null,
      isPending: false,
      isError: true,
    });
    render(<CloningPolicyPanel />);
    expect(
      screen.getByTestId("settings-cloning-policy-error"),
    ).toBeInTheDocument();
  });

  it("renders empty-policy message when no constraints are set", () => {
    useTenantCloningPolicyMock.mockReturnValueOnce({
      data: {
        allowedPolicies: null,
        allowedAttachmentModes: null,
        maxClones: null,
        maxDepth: null,
        budget: null,
      },
      isPending: false,
      isError: false,
    });
    render(<CloningPolicyPanel />);
    expect(
      screen.getByTestId("settings-cloning-policy-panel"),
    ).toBeInTheDocument();
    expect(screen.getByText(/No tenant-wide cloning constraints/)).toBeInTheDocument();
  });

  it("renders constraint badges when policy has allowedPolicies", () => {
    useTenantCloningPolicyMock.mockReturnValueOnce({
      data: {
        allowedPolicies: ["ephemeral-no-memory", "ephemeral-with-memory"],
        allowedAttachmentModes: ["detached"],
        maxClones: 3,
        maxDepth: null,
        budget: 1.5,
      },
      isPending: false,
      isError: false,
    });
    render(<CloningPolicyPanel />);
    expect(screen.getByText("ephemeral-no-memory")).toBeInTheDocument();
    expect(screen.getByText("ephemeral-with-memory")).toBeInTheDocument();
    expect(screen.getByText("detached")).toBeInTheDocument();
    expect(screen.getByText("3")).toBeInTheDocument();
    expect(screen.getByText("$1.50")).toBeInTheDocument();
  });
});
